using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

#pragma warning disable MA0004

namespace MtgSoloSports.Tests.Features.Reports;

/// <summary>
/// MSS-093: athlete archive (one Markdown per athlete + index) into
/// Athletes/ with eight stable color folders. HTTP contract tests through the
/// real pipeline with mocked Drive; reuses the library sync facilities, never
/// an independent uploader. Covers the full 2048-athlete fixture, pool
/// careers, duplicate names, renames, idempotency, resume, auto selective
/// refresh, record-holder deltas, cross-save isolation and OAuth errors.
/// </summary>
public sealed class GoogleDriveAthletePublishingTests
{
    private static readonly string[] ExpectedColors =
    [
        "White", "Blue", "Black", "Red", "Green", "Multicolor", "Hybrid", "Colorless",
    ];

    [Fact]
    public void AthleteSlot_MapsIdsWithoutCollision()
    {
        Guid saveId = Guid.NewGuid();
        var empty = new DrivePublishingStore.DestinationDocument(
            saveId, "Save", "folder-mtshistory", "mtshistory", null,
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        var withOne = DrivePublishingStore.WithAthleteSlot(empty, "123", "file-123", "athlete-123-foo.md", "fp-123", "t-123");
        var withTwo = DrivePublishingStore.WithAthleteSlot(withOne, "124", "file-124", "athlete-124-foo.md", "fp-124", "t-124");
        (string? id1, string? name1, string? fp1, _) = DrivePublishingStore.AthleteView(withTwo.AthleteByKey, "123");
        id1.ShouldBe("file-123");
        name1.ShouldBe("athlete-123-foo.md");
        fp1.ShouldBe("fp-123");
        (string? id2, _, _, _) = DrivePublishingStore.AthleteView(withTwo.AthleteByKey, "124");
        id2.ShouldBe("file-124");
        DrivePublishingStore.AthleteView(withTwo.AthleteByKey, "999").FileId.ShouldBeNull();
        var cleared = DrivePublishingStore.WithAthleteSlot(withTwo, "123", null, null, null, null);
        DrivePublishingStore.AthleteView(cleared.AthleteByKey, "123").FileId.ShouldBeNull();
        DrivePublishingStore.AthleteView(cleared.AthleteByKey, "124").FileId.ShouldBe("file-124");
        // Color folders are keyed by stable color name, never league.
        var withColor = DrivePublishingStore.WithAthleteColorFolder(empty, "White", "folder-white");
        withColor.AthleteColorFolderIds.ShouldNotBeNull();
        withColor.AthleteColorFolderIds!["White"].ShouldBe("folder-white");
    }

    [Fact]
    public async Task AthleteSync_CreatesFullArchiveIntoAdoptedHierarchy()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            await AssertPreviewWritesNothingAsync(client, saveId, drive);
            LibrarySyncResult first = await PostAthleteSyncAsync(client, saveId, allowCreateMissing: true, maxFiles: 5000);
            AssertFirstBulk(first, drive);
            await AssertAthletesMatchDownloadsAsync(client, saveId, drive, first, sampleSize: 5);
            await AssertRepeatWritesNothingAsync(client, saveId, drive);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task AssertPreviewWritesNothingAsync(HttpClient client, Guid saveId, FakeDriveClient drive)
    {
        LibrarySyncResult preview = await PostAthletePreviewAsync(client, saveId, allowCreateMissing: true);
        preview.Status.ShouldBe(HttpStatusCode.OK);
        preview.DryRun.ShouldBeTrue();
        preview.TotalItems.ShouldBeGreaterThan(2000);
        drive.FoldersFor("folder-mtshistory", "Athletes").ShouldBeEmpty();
        drive.Files.Values.Where(f => !string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    private static void AssertFirstBulk(LibrarySyncResult first, FakeDriveClient drive)
    {
        first.Status.ShouldBe(HttpStatusCode.OK);
        first.DryRun.ShouldBeFalse();
        first.Truncated.ShouldBeFalse();
        first.TotalItems.ShouldBeGreaterThan(2000);
        first.Files.Count.ShouldBe(first.TotalItems);
        first.Files.ShouldAllBe(f => string.Equals(f.Outcome, "created", StringComparison.Ordinal));
        first.CategoryTotal("athlete").ShouldBeGreaterThan(2000);
        first.CategoryTotal("athletes-index").ShouldBe(1);
        string athletesId = drive.FoldersFor("folder-mtshistory", "Athletes").Single();
        foreach (string color in ExpectedColors)
        {
            drive.FoldersFor(athletesId, color).Count.ShouldBe(1);
        }

        foreach (AthleteFileRow file in first.AthleteFiles())
        {
            file.FileName.ShouldStartWith($"athlete-{file.AthleteId}-");
            file.FileName.ShouldEndWith(".md");
            file.FileId.ShouldNotBeNullOrWhiteSpace();
        }

        HashSet<string> fileIds = first.Files.Select(f => f.FileId!).ToHashSet(StringComparer.Ordinal);
        fileIds.Count.ShouldBe(first.Files.Count);
        string indexId = first.FileId("athletes-index");
        drive.Files[indexId].Content.ShouldContain("# Athletes index");
        drive.Files[indexId].Content.ShouldContain("Common pool");
        drive.Files[indexId].Parents.Single().ShouldBe(athletesId);
    }

    private static async Task AssertRepeatWritesNothingAsync(HttpClient client, Guid saveId, FakeDriveClient drive)
    {
        int filesAfterFirst = drive.Files.Count;
        LibrarySyncResult second = await PostAthleteSyncAsync(client, saveId, maxFiles: 5000);
        second.Files.ShouldAllBe(f => string.Equals(f.Outcome, "unchanged", StringComparison.Ordinal));
        drive.Files.Count.ShouldBe(filesAfterFirst);
    }

    [Fact]
    public async Task AthleteSync_ReusesAdoptedFilesAndRetainsMappingOnRename()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            // Discover one athlete key from preview, then hand-upload a custom file.
            LibrarySyncResult preview = await PostAthletePreviewAsync(client, saveId, allowCreateMissing: true);
            string athleteReport = preview.ReportsForCategory("athlete").First();
            string athleteKey = athleteReport.Substring("athlete:".Length);
            drive.SeedFolder("folder-athletes", "folder-mtshistory", "Athletes");
            string color = await GetAthleteColorAsync(factory, root, saveId, int.Parse(athleteKey, System.Globalization.CultureInfo.InvariantCulture));
            drive.SeedFolder("folder-color", "folder-athletes", color);
            drive.SeedFile("athlete-adopted", "folder-color", "my-athlete.md", "# old athlete");
            await PutDestinationAsync(client, saveId,
                "{\"mtshistoryFolderId\":\"folder-mtshistory\"," +
                "\"athleteAdoptions\":{\"" + athleteKey + "\":{\"fileId\":\"athlete-adopted\",\"fileName\":\"my-athlete.md\"}}}");

            LibrarySyncResult synced = await PostAthleteSyncSelectiveAsync(client, saveId, [athleteReport, "athletes-index"], allowCreateMissing: true);
            synced.Status.ShouldBe(HttpStatusCode.OK);
            synced.FileId(athleteReport).ShouldBe("athlete-adopted");
            synced.Outcome(athleteReport).ShouldBe("updated");
            drive.Files["athlete-adopted"].Name.ShouldBe("my-athlete.md");
            drive.Files["athlete-adopted"].Content.ShouldContain("# Athlete report");
            // Index must link the stored/adopted filename, not a freshly slugged name.
            string indexId = synced.FileId("athletes-index");
            drive.Files[indexId].Content.ShouldContain(color + "/my-athlete.md");

            // Rename the athlete in the save: mapping retains the file id, no duplicate.
            await RenameAthleteAsync(factory, root, saveId, int.Parse(athleteKey, System.Globalization.CultureInfo.InvariantCulture), "Renamed Superstar");
            LibrarySyncResult afterRename = await PostAthleteSyncSelectiveAsync(client, saveId, [athleteReport, "athletes-index"], allowCreateMissing: true);
            afterRename.FileId(athleteReport).ShouldBe("athlete-adopted");
            drive.Files["athlete-adopted"].Content.ShouldContain("Renamed Superstar");
            drive.Files.Values.Count(f => f.Parents.Contains("folder-color", StringComparer.Ordinal)).ShouldBe(1);
            // Index still links the retained filename after rename, never the new slug.
            string renamedIndexId = afterRename.FileId("athletes-index");
            string renamedIndex = drive.Files[renamedIndexId].Content;
            renamedIndex.ShouldContain(color + "/my-athlete.md");
            renamedIndex.ShouldNotContain("renamed-superstar");
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteSync_DuplicateNamesUseStableIdsWithoutCollision()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            // Force two athletes to share a display name; filenames must stay distinct via ids.
            (int firstId, int secondId) = await DuplicateAthleteNamesAsync(factory, root, saveId);
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult synced = await PostAthleteSyncSelectiveAsync(
                client, saveId,
                [DriveLibraryKind.AthleteReport(firstId), DriveLibraryKind.AthleteReport(secondId), DriveLibraryKind.AthletesIndex],
                allowCreateMissing: true);
            synced.Status.ShouldBe(HttpStatusCode.OK);
            string firstFile = synced.FileName(DriveLibraryKind.AthleteReport(firstId));
            string secondFile = synced.FileName(DriveLibraryKind.AthleteReport(secondId));
            (string.Equals(firstFile, secondFile, StringComparison.Ordinal)).ShouldBeFalse();
            firstFile.ShouldStartWith($"athlete-{firstId}-");
            secondFile.ShouldStartWith($"athlete-{secondId}-");
            (string.Equals(synced.FileId(DriveLibraryKind.AthleteReport(firstId)), synced.FileId(DriveLibraryKind.AthleteReport(secondId)), StringComparison.Ordinal)).ShouldBeFalse();
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteSync_TruncatedResumesWithoutReupload()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult page1 = await PostAthleteSyncAsync(client, saveId, allowCreateMissing: true, maxFiles: 10, skip: 0);
            page1.Truncated.ShouldBeTrue();
            page1.NextSkip.ShouldBe(10);
            page1.Files.Count.ShouldBe(10);

            LibrarySyncResult page2 = await PostAthleteSyncAsync(client, saveId, allowCreateMissing: true, maxFiles: 10, skip: page1.NextSkip);
            page2.Files.Count.ShouldBe(10);
            // Pages cover distinct reports exactly once.
            HashSet<string> page1Reports = page1.Files.Select(f => f.Report).ToHashSet(StringComparer.Ordinal);
            foreach (AthleteFileRow row in page2.AthleteFiles())
            {
                page1Reports.ShouldNotContain(row.Report, StringComparer.Ordinal);
            }

            // Resume from start: settled files report unchanged, no duplicates.
            LibrarySyncResult retry1 = await PostAthleteSyncAsync(client, saveId, allowCreateMissing: true, maxFiles: 10, skip: 0);
            retry1.Files.ShouldAllBe(f => string.Equals(f.Outcome, "unchanged", StringComparison.Ordinal));
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteSync_PartialFailureRetrySucceeds()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult preview = await PostAthletePreviewAsync(client, saveId, allowCreateMissing: true);
            string victim = preview.ReportsForCategory("athlete").First();
            // Fail the update path: adopt then break updates for that file.
            string victimKey = victim.Substring("athlete:".Length);
            int victimId = int.Parse(victimKey, System.Globalization.CultureInfo.InvariantCulture);
            string victimColor = await GetAthleteColorAsync(factory, root, saveId, victimId);
            drive.SeedFolder("folder-athletes", "folder-mtshistory", "Athletes");
            drive.SeedFolder("folder-color", "folder-athletes", victimColor);
            drive.SeedFile("victim-file", "folder-color", $"athlete-{victimId}-victim.md", "# stale");
            await PutDestinationAsync(client, saveId,
                "{\"mtshistoryFolderId\":\"folder-mtshistory\"," +
                "\"athleteAdoptions\":{\"" + victimKey + "\":{\"fileId\":\"victim-file\",\"fileName\":null}}}");
            drive.FailUpdatesFor.Add("victim-file");

            LibrarySyncResult failed = await PostAthleteSyncSelectiveAsync(client, saveId, [victim, "athletes-index"]);
            failed.Outcome(victim).ShouldBe("failed");

            drive.FailUpdatesFor.Remove("victim-file");
            LibrarySyncResult retried = await PostAthleteSyncSelectiveAsync(client, saveId, [victim, "athletes-index"]);
            retried.Outcome(victim).ShouldBe("updated");
            drive.Files["victim-file"].Content.ShouldContain("# Athlete report");
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteSync_ChangedVsUnchangedAfterStage()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            LibrarySyncResult first = await PostAthleteSyncAsync(client, saveId, allowCreateMissing: true, maxFiles: 5000);
            first.Status.ShouldBe(HttpStatusCode.OK);
            Dictionary<string, string> before = SnapshotContents(drive);

            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Selective auto-style refresh: changed athlete keys + index.
            // Keys may cover many active athletes, but Drive writes stay
            // selective via fingerprints (pool reports report unchanged).
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            using var scope = factory.Services.CreateScope();
            DriveLibrarySyncHandler library = scope.ServiceProvider.GetRequiredService<DriveLibrarySyncHandler>();
            DrivePublishingStore.DestinationDocument? dest = await store.GetDestinationAsync(saveId, CancellationToken.None);
            IReadOnlyList<string> keys = await library.RecentAthleteKeysForAutoAsync(saveId, dest?.LastSeasonNumber, CancellationToken.None);
            keys.Count.ShouldBeGreaterThan(0);
            keys.ShouldContain(DriveLibraryKind.AthletesIndex, StringComparer.Ordinal);
            keys.Count.ShouldBeLessThanOrEqualTo(first.TotalItems);

            LibrarySyncResult selective = await PostAthleteSyncSelectiveAsync(client, saveId, keys, maxFiles: 5000);
            selective.Status.ShouldBe(HttpStatusCode.OK);
            selective.Files.Any(f => string.Equals(f.Outcome, "updated", StringComparison.Ordinal)).ShouldBeTrue();
            selective.Files.Any(f => string.Equals(f.Outcome, "unchanged", StringComparison.Ordinal)).ShouldBeTrue();
            drive.Files.Count.ShouldBeLessThanOrEqualTo(before.Count + keys.Count);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteSync_AmbiguousDuplicateNameStaysSafe()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            LibrarySyncResult preview = await PostAthletePreviewAsync(client, saveId, allowCreateMissing: true);
            string victim = preview.ReportsForCategory("athlete").First();
            int victimId = int.Parse(victim.Substring("athlete:".Length), System.Globalization.CultureInfo.InvariantCulture);
            string victimColor = await GetAthleteColorAsync(factory, root, saveId, victimId);
            string athletesId = (await PostAthleteSyncSelectiveAsync(client, saveId, ["athletes-index"], allowCreateMissing: true)).Files
                .Select(f => drive.Files.TryGetValue(f.FileId ?? string.Empty, out FakeDriveClient.FakeFile? file) ? file.Parents.FirstOrDefault() : null)
                .Where(p => p is not null).FirstOrDefault() ?? "folder-mtshistory";
            // Seed an ambiguous (1) duplicate alongside the canonical name.
            string canonical = (await GetAthleteFileNameAsync(client, saveId, victimId)).Trim();
            string variant = canonical.Insert(canonical.LastIndexOf('.'), " (1)");
            string colorFolder = drive.FoldersFor(athletesId, victimColor).SingleOrDefault() ?? athletesId;
            drive.SeedFile("dup-canonical", colorFolder, canonical, "# stale canonical");
            drive.SeedFile("dup-suffixed", colorFolder, variant, "# duplicate");

            LibrarySyncResult sync = await PostAthleteSyncSelectiveAsync(client, saveId, [victim]);
            sync.Outcome(victim).ShouldBe("failed");
            sync.Files.Single().Detail.ShouldNotBeNull();
            sync.Files.Single().Detail!.ShouldContain("(1)");
            drive.Files.Count.ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteSync_CrossSaveReuseNeedsConfirmation()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            await store.SaveDestinationAsync(new DrivePublishingStore.DestinationDocument(
                Guid.NewGuid(), "Other save", "folder-mtshistory", "mtshistory", null,
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null), CancellationToken.None);
            using HttpResponseMessage conflict = await client.PutAsync(
                $"/api/saves/{saveId:D}/reports/drive/destination",
                new StringContent("""{"mtshistoryFolderId":"folder-mtshistory"}""", Encoding.UTF8, "application/json"));
            conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory","confirmCrossSave":true}""");
            await PostAthleteSyncAsync(client, saveId, allowCreateMissing: true, maxFiles: 5, confirmCrossSave: true);
            drive.FoldersFor("folder-mtshistory", "Athletes").Count.ShouldBe(1);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteSync_VerifyReportsAthleteFolders()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            Dictionary<string, string> checks = await PostVerifyAsync(client, saveId);
            checks["athletes"].ShouldBe("ok");
            await PostAthleteSyncAsync(client, saveId, allowCreateMissing: true, maxFiles: 5);
            Dictionary<string, string> after = await PostVerifyAsync(client, saveId);
            after["athletes"].ShouldBe("ok");
            after["athletes-white"].ShouldBe("ok");
            after["athletes-index"].ShouldBe("ok");
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteSync_UnknownCategoryRejected()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            using HttpResponseMessage response = await client.PostAsync(
                $"/api/saves/{saveId:D}/reports/drive/library/sync",
                new StringContent("""{"confirmCrossSave":false,"allowOverwriteEditedRemote":false,"allowCreateMissing":false,"categories":["bogus"]}""", Encoding.UTF8, "application/json"));
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteAuto_OrdinaryStageDoesNotReuploadPoolCorpus()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            // Auto dossier publish needs Seasons/ + almanac; create them first.
            await PostPublishAsync(client, saveId, allowCreateMissing: true);
            await PostAthleteSyncAsync(client, saveId, allowCreateMissing: true, maxFiles: 5000);
            int filesAfterFull = drive.Files.Count;
            await PutAutoPublishAsync(client, saveId, true);

            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 60);

            // Auto mode settled dossier/almanac plus selective athletes: no full re-upload.
            drive.Files.Count.ShouldBeLessThan(filesAfterFull + 100);
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? dest = await store.GetDestinationAsync(saveId, CancellationToken.None);
            dest.ShouldNotBeNull();
            dest!.AthleteByKey.ShouldNotBeNull();
            dest.AthleteByKey!.Count.ShouldBeGreaterThan(2000);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task PostPublishAsync(HttpClient client, Guid saveId, bool allowCreateMissing)
    {
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/publish",
            new StringContent(JsonSerializer.Serialize(new { confirmCrossSave = false, allowOverwriteEditedRemote = false, allowCreateMissing }), Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AthleteAuto_BulkSimulateCoversChangedRange()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            using var scope = factory.Services.CreateScope();
            DriveLibrarySyncHandler library = scope.ServiceProvider.GetRequiredService<DriveLibrarySyncHandler>();
            IReadOnlyList<string> before = await library.RecentAthleteKeysForAutoAsync(saveId, lastPublishedSeason: null, CancellationToken.None);
            before.ShouldContain(DriveLibraryKind.AthletesIndex, StringComparer.Ordinal);

            using HttpResponseMessage bulk = await client.PostAsync(
                $"/api/saves/{saveId:D}/simulate-seasons",
                new StringContent("""{"seasons":1}""", Encoding.UTF8, "application/json"));
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);

            IReadOnlyList<string> after = await library.RecentAthleteKeysForAutoAsync(saveId, lastPublishedSeason: 2, CancellationToken.None);
            after.ShouldContain(DriveLibraryKind.AthletesIndex, StringComparer.Ordinal);
            after.Count.ShouldBeGreaterThan(0);
            // Coalesced bulk keys are deduplicated stable report keys.
            after.Distinct(StringComparer.Ordinal).Count().ShouldBe(after.Count);
            after.ShouldAllBe(k => k.StartsWith("athlete:", StringComparison.Ordinal) || string.Equals(k, "athletes-index", StringComparison.Ordinal));
            _ = drive;
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AthleteRecordHolder_PreviousHolderIncludedOnMaximaChange()
    {
        var (factory, root, _, _) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            SaveStore store = factory.Services.GetRequiredService<SaveStore>();
            using SaveDbContext context = store.OpenDbContext(saveId);
            var maxima = await MtgSoloSports.Features.Athletes.GetRecordHoldings.GetAthleteRecordHoldingsHandler.LoadGlobalMaximaAsync(context, CancellationToken.None);
            var holders = await DriveAthleteReports.LoadRecordHolderIdsAsync(context, maxima, CancellationToken.None);
            holders.Count.ShouldBeGreaterThan(0);

            // Simulate a new outright maxima: previous holders must be returned as deltas.
            var raised = new MtgSoloSports.Features.Athletes.GetRecordHoldings.GetAthleteRecordHoldingsHandler.GlobalMaxima(
                maxima.FeederTitles, maxima.SuperleagueTitles, maxima.TotalTitles,
                maxima.StageWins + 1, maxima.RoundWins, maxima.SuperleagueAppearances, maxima.TotalAppearances,
                maxima.LongestTenure, maxima.Promotions, maxima.Relegations, maxima.EffectiveBonus,
                maxima.TitleStreak, maxima.StageWinStreak);
            var oldMap = DriveAthleteReports.ToMaximaMap(maxima);
            HashSet<int> changed = await DriveAthleteReports.LoadChangedAthleteIdsAsync(context, 1, raised, oldMap, CancellationToken.None);
            foreach (int holder in holders)
            {
                changed.ShouldContain(holder);
            }
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    // Helpers (same fake shape as MSS-091 library tests).

    private sealed record LibrarySyncResult(
        HttpStatusCode Status,
        bool DryRun,
        bool Truncated,
        int TotalItems,
        int? NextSkip,
        IReadOnlyList<DriveFileOutcome> Files)
    {
        public string Outcome(string report) => Files.Single(f => string.Equals(f.Report, report, StringComparison.Ordinal)).Outcome;

        public string FileId(string report) => Files.Single(f => string.Equals(f.Report, report, StringComparison.Ordinal)).FileId!;

        public string FileName(string report) => Files.Single(f => string.Equals(f.Report, report, StringComparison.Ordinal)).FileName!;

        public int CategoryTotal(string category) => Files.Count(f => string.Equals(DriveLibraryKind.CategoryOf(f.Report), category, StringComparison.Ordinal));

        public List<string> ReportsForCategory(string category) =>
            Files.Where(f => string.Equals(DriveLibraryKind.CategoryOf(f.Report), category, StringComparison.Ordinal)).Select(f => f.Report).ToList();

        public List<AthleteFileRow> AthleteFiles() =>
            Files.Where(f => f.Report.StartsWith("athlete:", StringComparison.Ordinal)).Select(f =>
                new AthleteFileRow(f.Report, int.Parse(f.Report.Substring("athlete:".Length), System.Globalization.CultureInfo.InvariantCulture), f.FileId!, f.FileName!)).ToList();
    }

    private sealed record AthleteFileRow(string Report, int AthleteId, string FileId, string FileName);

    private static async Task<LibrarySyncResult> PostAthletePreviewAsync(HttpClient client, Guid saveId, bool allowCreateMissing = false)
    {
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/library/preview",
            new StringContent(JsonSerializer.Serialize(new { confirmCrossSave = false, allowOverwriteEditedRemote = false, allowCreateMissing, categories = new[] { "athlete", "athletes-index" } }), Encoding.UTF8, "application/json"));
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        return new LibrarySyncResult(
            response.StatusCode,
            root.GetProperty("dryRun").GetBoolean(),
            root.GetProperty("truncated").GetBoolean(),
            root.GetProperty("totalItems").GetInt32(),
            root.TryGetProperty("nextSkip", out JsonElement skip) && skip.ValueKind == JsonValueKind.Number ? skip.GetInt32() : null,
            root.GetProperty("files").EnumerateArray().Select(e => new DriveFileOutcome(
                e.GetProperty("report").GetString()!,
                e.GetProperty("outcome").GetString()!,
                e.TryGetProperty("fileId", out JsonElement id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                e.TryGetProperty("fileName", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                null, null,
                e.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null)).ToList());
    }

    private static async Task<LibrarySyncResult> PostAthleteSyncAsync(HttpClient client, Guid saveId, bool allowCreateMissing = false, int? maxFiles = null, int? skip = null, bool confirmCrossSave = false)
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["confirmCrossSave"] = confirmCrossSave,
            ["allowOverwriteEditedRemote"] = false,
            ["allowCreateMissing"] = allowCreateMissing,
            ["categories"] = new[] { "athlete", "athletes-index" },
        };
        if (maxFiles.HasValue)
        {
            payload["maxFiles"] = maxFiles.Value;
        }

        if (skip.HasValue)
        {
            payload["skip"] = skip.Value;
        }

        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/library/sync",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        return new LibrarySyncResult(
            response.StatusCode,
            root.GetProperty("dryRun").GetBoolean(),
            root.GetProperty("truncated").GetBoolean(),
            root.GetProperty("totalItems").GetInt32(),
            root.TryGetProperty("nextSkip", out JsonElement next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null,
            root.GetProperty("files").EnumerateArray().Select(e => new DriveFileOutcome(
                e.GetProperty("report").GetString()!,
                e.GetProperty("outcome").GetString()!,
                e.TryGetProperty("fileId", out JsonElement id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                e.TryGetProperty("fileName", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                null, null,
                e.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null)).ToList());
    }

    private static async Task<LibrarySyncResult> PostAthleteSyncSelectiveAsync(HttpClient client, Guid saveId, IReadOnlyList<string> onlyKeys, bool allowCreateMissing = false, int? maxFiles = null)
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal)
        {
            ["confirmCrossSave"] = false,
            ["allowOverwriteEditedRemote"] = false,
            ["allowCreateMissing"] = allowCreateMissing,
            ["categories"] = new[] { "athlete", "athletes-index" },
            ["onlyKeys"] = onlyKeys,
        };
        if (maxFiles.HasValue)
        {
            payload["maxFiles"] = maxFiles.Value;
        }

        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/library/sync",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        return new LibrarySyncResult(
            response.StatusCode,
            root.GetProperty("dryRun").GetBoolean(),
            root.GetProperty("truncated").GetBoolean(),
            root.GetProperty("totalItems").GetInt32(),
            root.TryGetProperty("nextSkip", out JsonElement next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null,
            root.GetProperty("files").EnumerateArray().Select(e => new DriveFileOutcome(
                e.GetProperty("report").GetString()!,
                e.GetProperty("outcome").GetString()!,
                e.TryGetProperty("fileId", out JsonElement id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                e.TryGetProperty("fileName", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                null, null,
                e.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null)).ToList());
    }

    private static Dictionary<string, string> SnapshotContents(FakeDriveClient drive) =>
        drive.Files.Where(kv => !string.Equals(kv.Value.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Content, StringComparer.Ordinal);

    private static async Task AssertAthletesMatchDownloadsAsync(HttpClient client, Guid saveId, FakeDriveClient drive, LibrarySyncResult sync, int sampleSize)
    {
        foreach (AthleteFileRow row in sync.AthleteFiles().Take(sampleSize))
        {
            using HttpResponseMessage download = await client.GetAsync($"/api/saves/{saveId:D}/athletes/{row.AthleteId}/report?format=md");
            download.StatusCode.ShouldBe(HttpStatusCode.OK);
            string downloadBody = await download.Content.ReadAsStringAsync();
            string driveBody = drive.Files[row.FileId].Content;
            Normalize(driveBody).ShouldBe(Normalize(downloadBody));
        }

        static string Normalize(string markdown)
        {
            StringBuilder sb = new();
            foreach (string line in markdown.Split('\n'))
            {
                if (line.TrimStart().StartsWith("Generated:", StringComparison.Ordinal))
                {
                    continue;
                }

                sb.Append(line.TrimEnd());
                sb.Append('\n');
            }

            return sb.ToString();
        }
    }

    private static async Task<string> GetAthleteColorAsync(WebApplicationFactory<Program> factory, string root, Guid saveId, int athleteId)
    {
        SaveStore store = factory.Services.GetRequiredService<SaveStore>();
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveAthleteEntity? athlete = await context.SaveAthletes.AsNoTracking().SingleOrDefaultAsync(e => e.Id == athleteId);
        athlete.ShouldNotBeNull();
        return ((MtgSoloSports.SimulationKernel.Catalog.SportingColor)athlete!.SportingColor).ToString();
    }

    private static async Task<string> GetAthleteFileNameAsync(HttpClient client, Guid saveId, int athleteId)
    {
        using HttpResponseMessage download = await client.GetAsync($"/api/saves/{saveId:D}/athletes/{athleteId}/report?format=md");
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        string? disposition = download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName;
        disposition.ShouldNotBeNullOrWhiteSpace();
        return disposition!.Trim('"');
    }

    private static async Task RenameAthleteAsync(WebApplicationFactory<Program> factory, string root, Guid saveId, int athleteId, string newName)
    {
        SaveStore store = factory.Services.GetRequiredService<SaveStore>();
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveAthleteEntity? athlete = await context.SaveAthletes.SingleOrDefaultAsync(e => e.Id == athleteId);
        athlete.ShouldNotBeNull();
        athlete!.Name = newName;
        await context.SaveChangesAsync();
    }

    private static async Task<(int First, int Second)> DuplicateAthleteNamesAsync(WebApplicationFactory<Program> factory, string root, Guid saveId)
    {
        SaveStore store = factory.Services.GetRequiredService<SaveStore>();
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<SaveAthleteEntity> athletes = await context.SaveAthletes.OrderBy(e => e.Id).Take(2).ToListAsync();
        athletes.Count.ShouldBe(2);
        // Unique names that slug to the same value: filenames must still be
        // distinct via stable ids (athlete-<id>-<slug>.md).
        athletes[0].Name = "Duplicate Slug Test!";
        athletes[1].Name = "Duplicate Slug Test?";
        await context.SaveChangesAsync();
        return (athletes[0].Id, athletes[1].Id);
    }

    private static async Task<Dictionary<string, string>> PostVerifyAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.PostAsync($"/api/saves/{saveId:D}/reports/drive/verify", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(e => e.GetProperty("report").GetString()!, e => e.GetProperty("status").GetString()!, StringComparer.Ordinal);
    }

    private static async Task SeedConnectionAsync(WebApplicationFactory<Program> factory)
    {
        DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
        await store.SaveConnectionAsync(new DrivePublishingStore.ConnectionDocument(
            "refresh-1", null, DriveScopes.DriveFile, DateTimeOffset.UtcNow, "access-seed", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);
    }

    private static async Task PutDestinationAsync(HttpClient client, Guid saveId, string json)
    {
        using HttpResponseMessage response = await client.PutAsync(
            $"/api/saves/{saveId:D}/reports/drive/destination",
            new StringContent(json, Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task PutAutoPublishAsync(HttpClient client, Guid saveId, bool enabled)
    {
        using HttpResponseMessage response = await client.PutAsync(
            $"/api/saves/{saveId:D}/reports/drive/auto-publish",
            new StringContent($"{{\"enabled\":{enabled.ToString().ToLowerInvariant()}}}", Encoding.UTF8, "application/json"));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed record AutoState(bool Enabled, string Status, int? LastStage);

    private static async Task<AutoState> GetAutoStateAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = json.RootElement;
        return new AutoState(
            root.GetProperty("autoPublishEnabled").GetBoolean(),
            root.GetProperty("autoPublishStatus").GetString()!,
            root.TryGetProperty("lastGlobalStage", out JsonElement last) && last.ValueKind == JsonValueKind.Number ? last.GetInt32() : null);
    }

    private static async Task WaitForAutoUpToDateAsync(HttpClient client, Guid saveId, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        AutoState latest = await GetAutoStateAsync(client, saveId);
        while (!string.Equals(latest.Status, "UpToDate", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException($"Timed out waiting for auto-publish UpToDate; last was {latest.Status}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
            latest = await GetAutoStateAsync(client, saveId);
        }
    }

    private static (WebApplicationFactory<Program> Factory, string Root, FakeOAuthClient OAuth, FakeDriveClient Drive) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-drive-athlete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FakeOAuthClient oauth = new();
        FakeDriveClient drive = new();
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
                services.Configure<DrivePublishingOptions>(o =>
                {
                    o.StorePath = Path.Combine(root, "drive-publishing");
                    o.GoogleClientId = "test-client";
                    o.GoogleClientSecret = "test-secret";
                });
                services.AddSingleton<IGoogleOAuthClient>(oauth);
                services.AddSingleton<IGoogleDriveClient>(drive);
            }));
        return (factory, root, oauth, drive);
    }

    private sealed class FakeOAuthClient : IGoogleOAuthClient
    {
        public string BuildAuthorizationUrl(string clientId, string redirectUri, string state, string codeChallenge) =>
            $"https://accounts.google.test/auth?client_id={clientId}&state={Uri.EscapeDataString(state)}&challenge={codeChallenge}";

        public Task<OAuthTokenResponse> ExchangeCodeAsync(string clientId, string clientSecret, string redirectUri, string code, string codeVerifier, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OAuthTokenResponse("access-initial", 3600, "refresh-1", DriveScopes.DriveFile));

        public Task<OAuthTokenResponse> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OAuthTokenResponse("access-refreshed", 3600, refreshToken, DriveScopes.DriveFile));

        public Task<bool> TryRevokeAsync(string token, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeDriveClient : IGoogleDriveClient
    {
        public readonly Dictionary<string, FakeFile> Files = new(StringComparer.Ordinal);
        public readonly HashSet<string> FailUpdatesFor = new(StringComparer.Ordinal);
        public readonly HashSet<string> DeniedIds = new(StringComparer.Ordinal);
        private int _nextId;
        private static long _stamp;

        public void SeedFile(string id, string parentId, string name, string content)
        {
            Files[id] = new FakeFile(id, name, "text/markdown", [parentId], content, Stamp(), false, $"https://drive.test/file/{id}");
        }

        public void SeedFolder(string id, string parentId, string name)
        {
            Files[id] = new FakeFile(id, name, "application/vnd.google-apps.folder", [parentId], string.Empty, Stamp(), false, $"https://drive.test/file/{id}");
        }

        public IReadOnlyList<string> FoldersFor(string parentId, string name) =>
            Files.Values.Where(f => !f.Trashed && string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)
                && f.Parents.Contains(parentId, StringComparer.Ordinal) && string.Equals(f.Name, name, StringComparison.Ordinal))
                .Select(f => f.Id).ToList();

        public Task<DriveFile?> GetFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default)
        {
            CheckDenied(fileId);
            Files.TryGetValue(fileId, out FakeFile? file);
            return Task.FromResult<DriveFile?>(file?.ToDriveFile());
        }

        public Task<string?> DownloadFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default)
        {
            CheckDenied(fileId);
            Files.TryGetValue(fileId, out FakeFile? file);
            if (file is not null && string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
            {
                throw new DriveApiException(HttpStatusCode.Forbidden, "Google Drive failed (403). fileNotDownloadable: Folders are not downloadable.");
            }

            return Task.FromResult(file?.Content);
        }

        public Task<IReadOnlyList<DriveFile>> ListByNameInFolderAsync(string accessToken, string folderId, string name, CancellationToken cancellationToken = default)
        {
            CheckDenied(folderId);
            IReadOnlyList<DriveFile> matches = Files.Values.Where(f => !f.Trashed
                    && f.Parents.Contains(folderId, StringComparer.Ordinal) && string.Equals(f.Name, name, StringComparison.Ordinal))
                .Select(f => f.ToDriveFile()).ToList();
            return Task.FromResult(matches);
        }

        public Task<DriveFile?> FindFolderAsync(string accessToken, string parentFolderId, string name, CancellationToken cancellationToken = default)
        {
            CheckDenied(parentFolderId);
            List<DriveFile> folders = Files.Values.Where(f => !f.Trashed
                    && string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)
                    && f.Parents.Contains(parentFolderId, StringComparer.Ordinal) && string.Equals(f.Name, name, StringComparison.Ordinal))
                .Select(f => f.ToDriveFile()).ToList();
            return folders.Count switch
            {
                0 => Task.FromResult<DriveFile?>(null),
                1 => Task.FromResult<DriveFile?>(folders[0]),
                _ => throw new DriveConflictException("ambiguous"),
            };
        }

        public Task<DriveFile> CreateFolderAsync(string accessToken, string parentFolderId, string name, CancellationToken cancellationToken = default)
        {
            CheckDenied(parentFolderId);
            string id = $"folder-{++_nextId}";
            FakeFile file = new(id, name, "application/vnd.google-apps.folder", [parentFolderId], string.Empty, Stamp(), false, $"https://drive.test/file/{id}");
            Files[id] = file;
            return Task.FromResult(file.ToDriveFile());
        }

        public Task<DriveFile> CreateMarkdownAsync(string accessToken, string folderId, string name, string content, CancellationToken cancellationToken = default)
        {
            CheckDenied(folderId);
            string id = $"file-{++_nextId}";
            FakeFile file = new(id, name, "text/markdown", [folderId], content, Stamp(), false, $"https://drive.test/file/{id}");
            Files[id] = file;
            return Task.FromResult(file.ToDriveFile());
        }

        public Task<DriveFile> UpdateContentAsync(string accessToken, string fileId, string content, string? newName, CancellationToken cancellationToken = default)
        {
            CheckDenied(fileId);
            if (FailUpdatesFor.Contains(fileId))
            {
                throw new DriveUpstreamException("simulated update failure");
            }

            FakeFile file = Files[fileId];
            FakeFile updated = file with { Content = content, Name = newName ?? file.Name, ModifiedTime = Stamp() };
            Files[fileId] = updated;
            return Task.FromResult(updated.ToDriveFile());
        }

        private void CheckDenied(string id)
        {
            if (DeniedIds.Contains(id))
            {
                throw new DriveApiException(HttpStatusCode.Forbidden, "simulated 403 grant missing");
            }
        }

        private static string Stamp() => $"2026-01-01T00:00:{Interlocked.Increment(ref _stamp):D4}Z";

        public sealed record FakeFile(string Id, string Name, string MimeType, List<string> Parents, string Content, string ModifiedTime, bool Trashed, string WebViewLink)
        {
            public DriveFile ToDriveFile() => new(Id, Name, MimeType, Parents, ModifiedTime, null, Trashed, WebViewLink);
        }
    }
}
