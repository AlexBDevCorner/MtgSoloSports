using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

/// <summary>
/// MSS-091: full historical library sync (season dossiers, Color/Type Cup
/// editions and team histories, almanac) into the organized Drive hierarchy.
/// HTTP contract tests through the real pipeline with a mocked Drive folder
/// tree; the same seeded saves the single-file publisher tests use, extended
/// with Cup folder coverage and multi-season catch-up.
/// </summary>
public sealed class GoogleDriveLibraryPublishingTests
{
    [Fact]
    public void LibrarySlot_MapsEditionAndTeamKeysWithoutCollision()
    {
        Guid saveId = Guid.NewGuid();
        var empty = new DrivePublishingStore.DestinationDocument(
            saveId, "Save", "folder-mtshistory", "mtshistory", null,
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        var withEdition = DrivePublishingStore.WithLibrarySlot(empty, true, "color:3", "file-e", "edition.md", "fp-e", "t-e");
        var withTeam = DrivePublishingStore.WithLibrarySlot(withEdition, false, "color:red", "file-t", "team.md", "fp-t", "t-t");
        (string? editionId, string? editionName, string? editionFp, _) =
            DrivePublishingStore.LibraryView(withTeam.CupEditionByKey, "color:3");
        editionId.ShouldBe("file-e");
        editionName.ShouldBe("edition.md");
        editionFp.ShouldBe("fp-e");
        (string? teamId, string? teamName, string? teamFp, _) =
            DrivePublishingStore.LibraryView(withTeam.CupTeamByKey, "color:red");
        teamId.ShouldBe("file-t");
        teamName.ShouldBe("team.md");
        teamFp.ShouldBe("fp-t");
        // Editions and teams never share a map: a team lookup in the edition
        // map misses instead of returning the wrong report.
        DrivePublishingStore.LibraryView(withTeam.CupEditionByKey, "color:red").FileId.ShouldBeNull();
        DrivePublishingStore.LibraryView(withTeam.CupTeamByKey, "color:3").FileId.ShouldBeNull();
        // Clearing one key keeps the other map intact.
        var cleared = DrivePublishingStore.WithLibrarySlot(withTeam, true, "color:3", null, null, null, null);
        DrivePublishingStore.LibraryView(cleared.CupEditionByKey, "color:3").FileId.ShouldBeNull();
        DrivePublishingStore.LibraryView(cleared.CupTeamByKey, "color:red").FileId.ShouldBe("file-t");
    }

    [Fact]
    public async Task LibrarySync_CreatesFullLibraryIntoAdoptedHierarchy()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            drive.SeedFolder("folder-seasons", "folder-mtshistory", "Seasons");
            drive.SeedFolder("folder-color-cup", "folder-mtshistory", "Color Cup");
            drive.SeedFolder("folder-color-cups", "folder-color-cup", "Cups");
            drive.SeedFolder("folder-color-teams", "folder-color-cup", "Teams Histories");
            drive.SeedFolder("folder-type-cup", "folder-mtshistory", "Type Cup");
            drive.SeedFolder("folder-type-cups", "folder-type-cup", "Cups");
            drive.SeedFolder("folder-type-teams", "folder-type-cup", "Teams Histories");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult first = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            first.Status.ShouldBe(HttpStatusCode.OK);
            first.DryRun.ShouldBeFalse();
            first.Truncated.ShouldBeFalse();
            first.TotalItems.ShouldBeGreaterThan(10);
            first.Files.Count.ShouldBe(first.TotalItems);
            first.Outcome("almanac").ShouldBe("created");
            // Every file is created on the first sync: no adopted ids exist yet.
            first.Files.ShouldAllBe(f => string.Equals(f.Outcome, "created", StringComparison.Ordinal));

            // Category routing: every supported report family is covered and
            // each file landed in its own folder (no parallel trees).
            first.CategoryTotal("almanac").ShouldBe(1);
            first.CategoryTotal("season-dossier").ShouldBe(2);
            first.CategoryTotal("color-edition").ShouldBeGreaterThanOrEqualTo(1);
            first.CategoryTotal("color-team").ShouldBe(8);
            await AssertHierarchyAdoptedAsync(factory, saveId, drive);

            int filesAfterFirst = drive.Files.Count;
            LibrarySyncResult second = await PostLibrarySyncAsync(client, saveId);
            second.Status.ShouldBe(HttpStatusCode.OK);
            second.Files.ShouldAllBe(f => string.Equals(f.Outcome, "unchanged", StringComparison.Ordinal));
            drive.Files.Count.ShouldBe(filesAfterFirst);
            // Stored matches the user-facing downloads.
            await AssertLibraryMatchesDownloadsAsync(client, saveId, drive);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibraryPreview_DryRunWritesNothing()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult preview = await PostLibraryPreviewAsync(client, saveId, allowCreateMissing: true);
            preview.Status.ShouldBe(HttpStatusCode.OK);
            preview.DryRun.ShouldBeTrue();
            preview.TotalItems.ShouldBeGreaterThan(10);
            // No Drive writes of any kind: no folders, no Markdown files.
            drive.FoldersFor("folder-mtshistory", "Seasons").ShouldBeEmpty();
            drive.Files.Values
                .Where(f => !string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
                .ShouldBeEmpty();
            // No destination adoption is persisted by a dry run.
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? after =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            after.ShouldNotBeNull();
            after!.SeasonsFolderId.ShouldBeNull();
            after.ColorCupsFolderId.ShouldBeNull();
            after.LastLibraryPublishedUtc.ShouldBeNull();
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_ReusesAdoptedFilesWithCustomNames()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            SeedLibraryFolders(drive);
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            // Discover stable semantic keys from a dry run, then hand-upload
            // custom-named files with stale content for adoption.
            LibrarySyncResult preview = await PostLibraryPreviewAsync(client, saveId, allowCreateMissing: true);
            preview.Status.ShouldBe(HttpStatusCode.OK);
            string editionReport = preview.ReportsForCategory("color-edition").First();
            string editionKey = "color:" + editionReport.Substring("color-edition:".Length);
            string teamReport = preview.ReportsForCategory("color-team").First();
            string teamKey = "color:" + teamReport.Substring("color-team:".Length);
            drive.SeedFile("dossier-adopted", "folder-seasons", "my-season.md", "# old dossier");
            drive.SeedFile("edition-adopted", "folder-color-cups", "my-edition.md", "# old edition");
            drive.SeedFile("team-adopted", "folder-color-teams", "my-team.md", "# old team");
            drive.SeedFile("almanac-adopted", "folder-mtshistory", "universe-almanac-newtest.md", "# old almanac");
            await PutDestinationAsync(client, saveId,
                "{\"mtshistoryFolderId\":\"folder-mtshistory\",\"dossierFileId\":\"dossier-adopted\",\"dossierFileName\":\"my-season.md\"," +
                "\"almanacFileId\":\"almanac-adopted\",\"almanacFileName\":\"universe-almanac-newtest.md\"," +
                "\"editionAdoptions\":{\"" + editionKey + "\":{\"fileId\":\"edition-adopted\",\"fileName\":\"my-edition.md\"}}," +
                "\"teamAdoptions\":{\"" + teamKey + "\":{\"fileId\":\"team-adopted\",\"fileName\":\"my-team.md\"}}}");

            LibrarySyncResult synced = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            synced.Status.ShouldBe(HttpStatusCode.OK);
            // The adopted current-season dossier resolves to season 2 (the
            // current season); adopted edition/team ids update in place.
            synced.FileId(editionReport).ShouldBe("edition-adopted");
            synced.Outcome(editionReport).ShouldBe("updated");
            synced.FileId(teamReport).ShouldBe("team-adopted");
            synced.Outcome(teamReport).ShouldBe("updated");
            synced.FileId("almanac").ShouldBe("almanac-adopted");
            synced.Outcome("almanac").ShouldBe("updated");
            // Custom remote names are preserved, never renamed to defaults.
            drive.Files["edition-adopted"].Name.ShouldBe("my-edition.md");
            drive.Files["team-adopted"].Name.ShouldBe("my-team.md");
            drive.Files["almanac-adopted"].Name.ShouldBe("universe-almanac-newtest.md");
            drive.Files["edition-adopted"].Content.ShouldContain("# Color Cup edition report");

            LibrarySyncResult second = await PostLibrarySyncAsync(client, saveId);
            second.Outcome(editionReport).ShouldBe("unchanged");
            second.Outcome(teamReport).ShouldBe("unchanged");
            second.Outcome("almanac").ShouldBe("unchanged");
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_ChangedVsUnchangedAfterStage()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            LibrarySyncResult first = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            first.Status.ShouldBe(HttpStatusCode.OK);
            Dictionary<string, string> before = SnapshotContents(drive);

            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            LibrarySyncResult second = await PostLibrarySyncAsync(client, saveId);
            second.Status.ShouldBe(HttpStatusCode.OK);
            // The current season dossier and almanac changed (career and
            // season sections moved on); the historical Cup archive did not.
            second.Outcome("almanac").ShouldBe("updated");
            second.Outcome("season-dossier:2").ShouldBe("updated");
            second.Outcome("color-edition:1").ShouldBe("unchanged");
            foreach (string team in second.ReportsForCategory("color-team"))
            {
                second.Outcome(team).ShouldBe("unchanged");
                drive.Files[second.FileId(team)!].Content.ShouldBe(before[second.FileId(team)!]);
            }

            int filesAfterSecond = drive.Files.Count;
            LibrarySyncResult third = await PostLibrarySyncAsync(client, saveId);
            third.Files.ShouldAllBe(f => string.Equals(f.Outcome, "unchanged", StringComparison.Ordinal));
            drive.Files.Count.ShouldBe(filesAfterSecond);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_UnexpectedPreexistingFile_NeedsAdoptionThenExplicitAdopt()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            SeedLibraryFolders(drive);
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult preview = await PostLibraryPreviewAsync(client, saveId, allowCreateMissing: true);
            string teamReport = preview.ReportsForCategory("color-team").First();
            string teamKey = "color:" + teamReport.Substring("color-team:".Length);
            string teamFileName = preview.FileName(teamReport)!;
            // A hand-uploaded file with the default name but unrelated
            // content: publishing must not overwrite it blindly.
            drive.SeedFile("hand-uploaded", "folder-color-teams", teamFileName, "# manually written by user");

            LibrarySyncResult blocked = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            blocked.Status.ShouldBe(HttpStatusCode.OK);
            blocked.Outcome(teamReport).ShouldBe("failed");
            blocked.Detail(teamReport)!.ShouldContain("already exists");
            blocked.Detail(teamReport)!.ShouldContain("hand-uploaded");
            drive.Files["hand-uploaded"].Content.ShouldBe("# manually written by user");
            drive.FileIn("folder-color-teams", teamFileName).Count.ShouldBe(1);

            // Explicit one-time adoption replaces deliberately on next sync.
            await PutDestinationAsync(client, saveId,
                "{\"mtshistoryFolderId\":\"folder-mtshistory\"," +
                "\"teamAdoptions\":{\"" + teamKey + "\":{\"fileId\":\"hand-uploaded\"}}}");
            LibrarySyncResult adopted = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            adopted.Outcome(teamReport).ShouldBe("updated");
            adopted.FileId(teamReport).ShouldBe("hand-uploaded");
            drive.Files["hand-uploaded"].Content.ShouldContain("# Color Cup team report");
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_DuplicateName_SurfacesWithoutCreation()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            SeedLibraryFolders(drive);
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult preview = await PostLibraryPreviewAsync(client, saveId, allowCreateMissing: true);
            string editionReport = preview.ReportsForCategory("color-edition").First();
            string editionFileName = preview.FileName(editionReport)!;
            // A manually duplicated upload (the "(1)" case): two same-named
            // competitors must surface, never multiply.
            drive.SeedFile("dup-1", "folder-color-cups", editionFileName, "a");
            drive.SeedFile("dup-2", "folder-color-cups", editionFileName, "b");

            LibrarySyncResult blocked = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            blocked.Status.ShouldBe(HttpStatusCode.OK);
            blocked.Outcome(editionReport).ShouldBe("failed");
            blocked.Detail(editionReport)!.ShouldContain("Adopt exactly one file id");
            drive.FileIn("folder-color-cups", editionFileName).Count.ShouldBe(2);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_PartialFailure_RetrySucceedsWithoutLosingProgress()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            _ = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);

            LibrarySyncResult probe = await PostLibraryPreviewAsync(client, saveId);
            string changedDossier = probe.Files
                .Where(f => !string.Equals(f.Outcome, "unchanged", StringComparison.Ordinal))
                .Select(f => f.Report)
                .First(r => r.StartsWith("season-dossier:", StringComparison.Ordinal));
            string dossierId = probe.FileId(changedDossier)!;
            string almanacId = probe.FileId("almanac")!;
            drive.FailUpdatesFor.Add(dossierId);

            LibrarySyncResult failed = await PostLibrarySyncAsync(client, saveId);
            failed.Outcome(changedDossier).ShouldBe("failed");
            failed.Outcome("almanac").ShouldBe("updated");
            // The successful almanac upload is kept; the failed dossier is
            // retried alone without re-uploading settled files.
            drive.FailUpdatesFor.Clear();
            int filesAfterFailure = drive.Files.Count;
            LibrarySyncResult retried = await PostLibrarySyncAsync(client, saveId);
            retried.Outcome(changedDossier).ShouldBe("updated");
            retried.FileId(changedDossier).ShouldBe(dossierId);
            retried.Outcome("almanac").ShouldBe("unchanged");
            retried.FileId("almanac").ShouldBe(almanacId);
            drive.Files.Count.ShouldBe(filesAfterFailure);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_OldSaveWithoutCups_PublishesOnlyDossiersAndAlmanac()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult synced = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            synced.Status.ShouldBe(HttpStatusCode.OK);
            synced.Files.ShouldAllBe(f => string.Equals(f.Outcome, "created", StringComparison.Ordinal));
            synced.Files.Select(f => f.Report).ShouldNotContain(r => r.StartsWith("color-", StringComparison.Ordinal));
            synced.Files.Select(f => f.Report).ShouldNotContain(r => r.StartsWith("type-", StringComparison.Ordinal));
            synced.CategoryTotal("season-dossier").ShouldBe(1);
            synced.CategoryTotal("almanac").ShouldBe(1);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_MultiSeasonCatchUp_CoversTwoCupSourceSeasons()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            // Named action: simulate one more full season (league plus its
            // postseason Cups) so the library must catch up multiple Cup
            // source seasons at once.
            using HttpResponseMessage simulated = await client.PostAsync(
                $"/api/saves/{saveId:D}/simulate-seasons",
                new StringContent("""{"seasons":1}""", Encoding.UTF8, "application/json"));
            simulated.StatusCode.ShouldBe(HttpStatusCode.OK);

            LibrarySyncResult synced = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            synced.Status.ShouldBe(HttpStatusCode.OK);
            List<string> editions = synced.Files
                .Select(f => f.Report)
                .Where(r => r.StartsWith("color-edition:", StringComparison.Ordinal)
                    || r.StartsWith("type-edition:", StringComparison.Ordinal))
                .ToList();
            editions.Select(e => e.Split(':')[1]).Distinct(StringComparer.Ordinal).Count().ShouldBeGreaterThanOrEqualTo(2);
            synced.CategoryTotal("season-dossier").ShouldBeGreaterThanOrEqualTo(3);

            LibrarySyncResult second = await PostLibrarySyncAsync(client, saveId);
            second.Files.ShouldAllBe(f => string.Equals(f.Outcome, "unchanged", StringComparison.Ordinal));
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_CupFinalization_UpdatesOnlySelectedReports()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            // Named action: select the Color Cup field for season 1.
            using HttpResponseMessage selected = await client.PostAsync(
                $"/api/saves/{saveId:D}/cups/color/teams", null);
            selected.StatusCode.ShouldBe(HttpStatusCode.OK);
            LibrarySyncResult beforeFinal = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            beforeFinal.Outcome("color-edition:1").ShouldBe("created");
            Dictionary<string, string> contentBefore = SnapshotContents(drive);

            // Named action: run the Color Cup team final.
            using HttpResponseMessage final = await client.PostAsync(
                $"/api/saves/{saveId:D}/cups/color/team", null);
            final.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Selective refresh of just the affected Cup reports: the edition
            // and all eight team histories update, while the dossier and
            // almanac they were not asked to cover stay byte-identical.
            LibrarySyncResult selective = await AssertSelectiveCupRefreshAsync(
                client, saveId, drive, beforeFinal, contentBefore);

            // A full sync afterwards refreshes the dossier/almanac (the Cup
            // results changed them) and leaves the just-synced Cup files alone.
            LibrarySyncResult full = await PostLibrarySyncAsync(client, saveId);
            full.Outcome("season-dossier:1").ShouldBe("updated");
            full.Outcome("almanac").ShouldBe("updated");
            full.Outcome("color-edition:1").ShouldBe("unchanged");
            foreach (string team in full.ReportsForCategory("color-team"))
            {
                full.Outcome(team).ShouldBe("unchanged");
            }
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task<LibrarySyncResult> AssertSelectiveCupRefreshAsync(
        HttpClient client,
        Guid saveId,
        FakeDriveClient drive,
        LibrarySyncResult beforeFinal,
        Dictionary<string, string> contentBefore)
    {
        List<string> cupKeys = beforeFinal.Files
            .Select(f => f.Report)
            .Where(r => r.StartsWith("color-edition:", StringComparison.Ordinal)
                || r.StartsWith("color-team:", StringComparison.Ordinal))
            .ToList();
        cupKeys.Count.ShouldBe(9);
        LibrarySyncResult selective = await PostLibrarySyncSelectiveAsync(client, saveId, cupKeys).ConfigureAwait(false);
        selective.Status.ShouldBe(HttpStatusCode.OK);
        selective.Truncated.ShouldBeFalse();
        selective.Files.ShouldAllBe(f => string.Equals(f.Outcome, "updated", StringComparison.Ordinal));
        foreach (string key in cupKeys)
        {
            string.Equals(
                drive.Files[selective.FileId(key)!].Content,
                contentBefore[selective.FileId(key)!],
                StringComparison.Ordinal).ShouldBeFalse();
        }

        string dossierId = beforeFinal.FileId("season-dossier:1")!;
        string almanacId = beforeFinal.FileId("almanac")!;
        drive.Files[dossierId].Content.ShouldBe(contentBefore[dossierId]);
        drive.Files[almanacId].Content.ShouldBe(contentBefore[almanacId]);
        return selective;
    }

    [Fact]
    public async Task LibrarySync_TruncatedRequest_ResumesWithoutReupload()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult first = await PostLibrarySyncMaxFilesAsync(client, saveId, maxFiles: 3);
            first.Status.ShouldBe(HttpStatusCode.OK, first.Body);
            first.Truncated.ShouldBeTrue();
            first.Files.Count.ShouldBe(3);
            first.Files.ShouldAllBe(f => string.Equals(f.Outcome, "created", StringComparison.Ordinal));
            first.NextSkip.ShouldBe(3);
            first.TotalItems.ShouldBeGreaterThan(3);
            int createdIds = drive.Files.Count;
            // A truncated page must not claim the whole library is synced.
            (await GetLibraryPublishedUtcAsync(client, saveId)).ShouldBeNull();

            // Resume with the returned cursor: the second page advances past
            // the first batch instead of re-processing it.
            LibrarySyncResult second = await PostLibrarySyncSkipAsync(
                client, saveId, skip: first.NextSkip!.Value, allowCreateMissing: true);
            second.Status.ShouldBe(HttpStatusCode.OK, second.Body);
            second.Truncated.ShouldBeFalse();
            second.TotalItems.ShouldBe(first.TotalItems);
            second.Files.ShouldNotBeEmpty();
            second.Files.Select(f => f.Report)
                .Intersect(first.Files.Select(f => f.Report), StringComparer.Ordinal)
                .ShouldBeEmpty();
            second.Files.ShouldAllBe(f => string.Equals(f.Outcome, "created", StringComparison.Ordinal));
            drive.Files.Count.ShouldBeGreaterThan(createdIds);

            // A bounded retry with the same MaxFiles also advances: paging
            // through with the cursor reaches every item exactly once.
            List<string> paged = [.. first.Files.Select(f => f.Report), .. second.Files.Select(f => f.Report)];
            paged.Distinct(StringComparer.Ordinal).Count().ShouldBe(first.TotalItems);
            paged.Count.ShouldBe(first.TotalItems);

            LibrarySyncResult settled = await PostLibrarySyncAsync(client, saveId);
            settled.Status.ShouldBe(HttpStatusCode.OK, settled.Body);
            settled.Truncated.ShouldBeFalse();
            settled.Files.ShouldAllBe(f => string.Equals(f.Outcome, "unchanged", StringComparison.Ordinal));
            (await GetLibraryPublishedUtcAsync(client, saveId)).ShouldNotBeNull();
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_SelectiveNewCupEdition_CreatesWhenFoldersAdopted()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult first = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            first.Status.ShouldBe(HttpStatusCode.OK, first.Body);
            first.Truncated.ShouldBeFalse();

            // Named action: simulate one more season so a new Cup source
            // season introduces edition files with no stored slot (the auto
            // selective-refresh trigger).
            using HttpResponseMessage simulated = await client.PostAsync(
                $"/api/saves/{saveId:D}/simulate-seasons",
                new StringContent("""{"seasons":1}""", Encoding.UTF8, "application/json"));
            simulated.StatusCode.ShouldBe(HttpStatusCode.OK);

            // The adopted Cup folders are known after the first sync, so a
            // selective refresh with creation allowed (the auto path) creates
            // the new edition files instead of reporting failed.
            LibrarySyncResult preview = await PostLibraryPreviewAsync(client, saveId, allowCreateMissing: true);
            preview.Status.ShouldBe(HttpStatusCode.OK, preview.Body);
            HashSet<string> known = new(first.Files.Select(f => f.Report), StringComparer.Ordinal);
            List<string> fresh = preview.Files
                .Select(f => f.Report)
                .Where(r => !known.Contains(r))
                .ToList();
            fresh.ShouldNotBeEmpty();
            LibrarySyncResult selective = await PostLibrarySyncSelectiveCreateAsync(client, saveId, fresh);
            selective.Status.ShouldBe(HttpStatusCode.OK, selective.Body);
            selective.Files.Select(f => f.Report).ShouldBe(fresh);
            selective.Files.ShouldAllBe(f => string.Equals(f.Outcome, "created", StringComparison.Ordinal));
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_CrossSaveReuse_NeedsExplicitConfirmation()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-shared", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            await store.SaveDestinationAsync(new DrivePublishingStore.DestinationDocument(
                Guid.NewGuid(), "Other save", "folder-shared", "mtshistory", null,
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null), CancellationToken.None);
            // The shared folder is adopted with explicit confirmation; the
            // sync itself still refuses without its own confirmation.
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-shared","confirmCrossSave":true}""");
            using HttpResponseMessage conflict = await PostLibrarySyncRawAsync(
                client, saveId, """{"confirmCrossSave":false}""");
            conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            // Confirmed cross-save proceeds to folder resolution (which then
            // creates the missing hierarchy instead of conflicting on reuse).
            LibrarySyncResult accepted = await ParseLibraryResultAsync(await PostLibrarySyncRawAsync(
                client, saveId, """{"confirmCrossSave":true,"allowCreateMissing":true}"""));
            accepted.Status.ShouldBe(HttpStatusCode.OK);
            accepted.Files.ShouldNotBeEmpty();
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibraryVerify_ReportsCupFolderMapping()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            Dictionary<string, string> before = await PostVerifyAsync(client, saveId);
            // Unadopted Cup folders report not-found with auto-resolve
            // guidance (nothing is created by Verify).
            before["color-cups"].ShouldBe("not-found");
            before["color-teams"].ShouldBe("not-found");
            before["type-cups"].ShouldBe("not-found");
            before["type-teams"].ShouldBe("not-found");

            drive.SeedFolder("folder-color-cup", "folder-mtshistory", "Color Cup");
            drive.SeedFolder("folder-color-cups", "folder-color-cup", "Cups");
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","colorCupFolderId":"folder-color-cup","colorCupsFolderId":"folder-color-cups"}""");
            Dictionary<string, string> after = await PostVerifyAsync(client, saveId);
            after["color-cups"].ShouldBe("ok");
            after["color-cup"].ShouldBe("ok");
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_UnknownCategory_Rejected()
    {
        var (factory, root, _, _) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            using HttpResponseMessage response = await PostLibrarySyncRawAsync(
                client, saveId, """{"confirmCrossSave":false,"categories":["bogus"]}""");
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AutoPublish_OrdinaryStage_DoesNotReuploadTeamHistories()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            LibrarySyncResult baseline = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            baseline.Status.ShouldBe(HttpStatusCode.OK);
            int filesBefore = drive.Files.Count;
            await PutAutoPublishAsync(client, saveId, enabled: true);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);
            int stageBefore = (await GetAutoStateAsync(client, saveId)).RequestedStage!.Value;

            // Snapshot every team-history file: ordinary league work must
            // never rewrite the archive.
            List<string> teamIds = drive.Files.Values
                .Where(f => !string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)
                    && f.Name.Contains("team", StringComparison.Ordinal))
                .Select(f => f.Id)
                .ToList();
            teamIds.ShouldNotBeEmpty();
            Dictionary<string, string> teamBefore = teamIds.ToDictionary(id => id, id => drive.Files[id].Content, StringComparer.Ordinal);

            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            await WaitForAutoStageAsync(client, saveId, stageBefore + 1, timeoutSeconds: 60);

            drive.Files.Count.ShouldBe(filesBefore);
            foreach (string id in teamIds)
            {
                drive.Files[id].Content.ShouldBe(teamBefore[id]);
            }
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public void ComputeCupSeasonsBack_CoversBulkRangeWithoutExpandingOrdinaryStages()
    {
        DriveLibrarySyncHandler.ComputeCupSeasonsBack(6, 1).ShouldBe(6);
        DriveLibrarySyncHandler.ComputeCupSeasonsBack(5, 5).ShouldBe(1);
        DriveLibrarySyncHandler.ComputeCupSeasonsBack(2, 2).ShouldBe(1);
        DriveLibrarySyncHandler.ComputeCupSeasonsBack(4, null).ShouldBe(1);
        DriveLibrarySyncHandler.ComputeCupSeasonsBack(4, 10).ShouldBe(1);
        DriveLibrarySyncHandler.ComputeCupSeasonsBack(600, 1).ShouldBe(500);
    }

    [Fact]
    public async Task AutoCupKeys_BulkSimulateSeasons_CoversWholeChangedRange()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10); // Bulk 2-season run can exceed default 100s on loaded CI.
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            // Baseline current season before the supported bulk path.
            int beforeSeason = await GetCurrentSeasonAsync(client, saveId);
            beforeSeason.ShouldBeGreaterThanOrEqualTo(2);

            // Named action: bulk-simulate two more full seasons through the
            // production endpoint (single coalesced auto checkpoint).
            using HttpResponseMessage simulated = await client.PostAsync(
                $"/api/saves/{saveId:D}/simulate-seasons",
                new StringContent("""{"seasons":2}""", Encoding.UTF8, "application/json"));
            simulated.StatusCode.ShouldBe(HttpStatusCode.OK);
            int afterSeason = await GetCurrentSeasonAsync(client, saveId);
            afterSeason.ShouldBe(beforeSeason + 2);

            DriveLibrarySyncHandler library;
            IReadOnlyList<string> narrow;
            IReadOnlyList<string> bulk;
            using (IServiceScope scope = factory.Services.CreateScope())
            {
                library = scope.ServiceProvider.GetRequiredService<DriveLibrarySyncHandler>();
                narrow = await library.RecentCupKeysAsync(saveId, 1, CancellationToken.None);
                bulk = await library.RecentCupKeysForAutoAsync(saveId, beforeSeason, CancellationToken.None);
            }

            // The fixed one-season window misses the earlier bulk source
            // seasons; the auto range since the last settled publish covers
            // every changed Cup edition.
            bulk.Count.ShouldBeGreaterThan(narrow.Count);
            foreach (string key in narrow)
            {
                bulk.ShouldContain(key, StringComparer.Ordinal);
            }

            List<int> bulkEditionSeasons = bulk
                .Where(k => k.StartsWith("color-edition:", StringComparison.Ordinal)
                    || k.StartsWith("type-edition:", StringComparison.Ordinal))
                .Select(k => int.Parse(k.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
            bulkEditionSeasons.Count.ShouldBeGreaterThanOrEqualTo(3);
            bulkEditionSeasons.ShouldContain(beforeSeason);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_LegacyDossierSlot_OnlyServesCurrentSeason()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            SeedLibraryFolders(drive);
            // MSS-089 era adoption: single current-season dossier id with no
            // per-season map and no LastSeasonNumber yet (no publish happened).
            drive.SeedFile("legacy-dossier", "folder-seasons", "my-legacy.md", "# legacy current");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"legacy-dossier","dossierFileName":"my-legacy.md"}""");

            LibrarySyncResult synced = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            synced.Status.ShouldBe(HttpStatusCode.OK);
            // Season 1 (historical) must not overwrite the adopted
            // current-season file; it creates its own stable file.
            string season1File = synced.FileId("season-dossier:1")!;
            (string.Equals(season1File, "legacy-dossier", StringComparison.Ordinal)).ShouldBeFalse();
            synced.Outcome("season-dossier:1").ShouldBe("created");
            // The current season (2) reuses the legacy id in place.
            synced.FileId("season-dossier:2").ShouldBe("legacy-dossier");
            synced.Outcome("season-dossier:2").ShouldBe("updated");
            drive.Files["legacy-dossier"].Content.Contains("# Season dossier", StringComparison.Ordinal).ShouldBeTrue();
            drive.Files["legacy-dossier"].Content.Contains("Season 2", StringComparison.Ordinal).ShouldBeTrue();
            drive.Files[season1File].Content.Contains("Season 1", StringComparison.Ordinal).ShouldBeTrue();
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LibrarySync_SuffixedDuplicate_SurfacesWithoutCreation()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            SeedLibraryFolders(drive);
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            LibrarySyncResult preview = await PostLibraryPreviewAsync(client, saveId, allowCreateMissing: true);
            string editionReport = preview.ReportsForCategory("color-edition").First();
            string editionFileName = preview.FileName(editionReport)!;
            string variant = editionFileName.EndsWith(".md", StringComparison.Ordinal)
                ? editionFileName[..^3] + " (1).md"
                : editionFileName + " (1)";

            // Hand-uploaded "(1)" duplicate with no base file: creating next
            // to it would increase ambiguity, so surfacing wins.
            drive.SeedFile("dup-suffixed", "folder-color-cups", variant, "# duplicate");
            LibrarySyncResult blocked = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            blocked.Status.ShouldBe(HttpStatusCode.OK);
            blocked.Outcome(editionReport).ShouldBe("failed");
            blocked.Detail(editionReport)!.Contains("(1)", StringComparison.Ordinal).ShouldBeTrue();
            drive.FileIn("folder-color-cups", editionFileName).Count.ShouldBe(0);

            // Base appears alongside the "(1)": still ambiguous, never a
            // silent update of the base while the duplicate lingers.
            drive.SeedFile("base-file", "folder-color-cups", editionFileName, "# stale base");
            LibrarySyncResult blockedBoth = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            blockedBoth.Status.ShouldBe(HttpStatusCode.OK);
            blockedBoth.Outcome(editionReport).ShouldBe("failed");
            blockedBoth.Detail(editionReport)!.Contains("(1)", StringComparison.Ordinal).ShouldBeTrue();
            drive.Files["base-file"].Content.ShouldBe("# stale base");
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public void LiveCupHandlers_ExposePostCommitAutoPublishNotifier()
    {
        // Correction guard: all supported Live Cup completion paths must
        // accept the same post-commit notifier as the one-shot handlers so
        // auto mode can refresh Cup reports after round-by-round finals.
        foreach (Type handler in new[]
                 {
                     typeof(MtgSoloSports.Features.Cups.PlayColorCupIndividualRound.PlayColorCupIndividualRoundHandler),
                     typeof(MtgSoloSports.Features.Cups.PlayColorCupTeamRound.PlayColorCupTeamRoundHandler),
                     typeof(MtgSoloSports.Features.Cups.PlayTypeCupTeamRound.PlayTypeCupTeamRoundHandler),
                     typeof(MtgSoloSports.Features.Cups.FinishTypeCupTeamStage.FinishTypeCupTeamStageHandler),
                 })
        {
            bool hasNotifier = handler.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(DriveAutoPublishNotifier)));
            hasNotifier.ShouldBeTrue($"Live handler {handler.Name} must accept DriveAutoPublishNotifier.");
        }
    }

    [Fact]
    public async Task LiveColorIndividualFinalization_UpdatesOnlySelectedReports()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");

            using HttpResponseMessage selected = await client.PostAsync(
                $"/api/saves/{saveId:D}/cups/color/teams", null);
            selected.StatusCode.ShouldBe(HttpStatusCode.OK);
            LibrarySyncResult beforeFinal = await PostLibrarySyncAsync(client, saveId, allowCreateMissing: true);
            Dictionary<string, string> contentBefore = SnapshotContents(drive);

            await PlayColorIndividualToCompletionAsync(client, saveId);

            List<string> cupKeys = beforeFinal.Files
                .Select(f => f.Report)
                .Where(r => r.StartsWith("color-edition:", StringComparison.Ordinal)
                    || r.StartsWith("color-team:", StringComparison.Ordinal))
                .ToList();
            cupKeys.Count.ShouldBe(9);
            LibrarySyncResult selective = await PostLibrarySyncSelectiveAsync(client, saveId, cupKeys);
            selective.Files.ShouldAllBe(f => string.Equals(f.Outcome, "updated", StringComparison.Ordinal));
            bool anyChanged = false;
            foreach (string key in cupKeys)
            {
                string id = selective.FileId(key)!;
                if (!string.Equals(drive.Files[id].Content, contentBefore[id], StringComparison.Ordinal))
                {
                    anyChanged = true;
                    break;
                }
            }

            anyChanged.ShouldBeTrue();
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task PlayColorIndividualToCompletionAsync(HttpClient client, Guid saveId)
    {
        for (int guard = 0; guard < 40; guard++)
        {
            using HttpResponseMessage next = await client.PostAsync(
                $"/api/saves/{saveId:D}/cups/color/individual/rounds/next", null).ConfigureAwait(false);
            if (next.StatusCode != HttpStatusCode.OK)
            {
                string body = await next.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new Xunit.Sdk.XunitException($"Live round {guard} failed with {next.StatusCode}: {body}");
            }
            using JsonDocument json = JsonDocument.Parse(await next.Content.ReadAsStringAsync().ConfigureAwait(false));
            bool complete = (json.RootElement.TryGetProperty("complete", out JsonElement c) && c.GetBoolean())
                || (json.RootElement.TryGetProperty("Complete", out JsonElement cc) && cc.GetBoolean())
                || (json.RootElement.TryGetProperty("isComplete", out JsonElement ic) && ic.GetBoolean())
                || (json.RootElement.TryGetProperty("IsComplete", out JsonElement iC) && iC.GetBoolean());
            if (complete)
            {
                return;
            }
        }

        throw new Xunit.Sdk.XunitException("Live Color Cup individual did not complete within 40 rounds.");
    }

    private static async Task AssertHierarchyAdoptedAsync(
        WebApplicationFactory<Program> factory,
        Guid saveId,
        FakeDriveClient drive)
    {
        drive.FoldersFor("folder-mtshistory", "Seasons").Count.ShouldBe(1);
        drive.FoldersFor("folder-mtshistory", "Color Cup").Count.ShouldBe(1);
        drive.FoldersFor("folder-mtshistory", "Type Cup").Count.ShouldBe(1);
        drive.FoldersFor("folder-color-cup", "Cups").Count.ShouldBe(1);
        drive.FoldersFor("folder-color-cup", "Teams Histories").Count.ShouldBe(1);
        drive.FoldersFor("folder-type-cup", "Cups").Count.ShouldBe(1);
        drive.FoldersFor("folder-type-cup", "Teams Histories").Count.ShouldBe(1);
        // Adopted folders are persisted for later syncs and status.
        DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
        DrivePublishingStore.DestinationDocument? after =
            await store.GetDestinationAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        after.ShouldNotBeNull();
        after!.SeasonsFolderId.ShouldBe("folder-seasons");
        after.ColorCupFolderId.ShouldBe("folder-color-cup");
        after.ColorCupsFolderId.ShouldBe("folder-color-cups");
        after.ColorTeamsFolderId.ShouldBe("folder-color-teams");
        after.TypeCupFolderId.ShouldBe("folder-type-cup");
        after.TypeCupsFolderId.ShouldBe("folder-type-cups");
        after.TypeTeamsFolderId.ShouldBe("folder-type-teams");
        after.LastLibraryPublishedUtc.ShouldNotBeNull();
        after.LastLibraryFailed.ShouldBe(0);
    }

    private sealed record LibrarySyncFile(string Report, string Outcome, string? FileId, string? FileName, string? Detail);

    private sealed record LibrarySyncResult(
        HttpStatusCode Status,
        bool DryRun,
        bool Truncated,
        int TotalItems,
        int? NextSkip,
        List<LibrarySyncFile> Files,
        string Body)
    {
        public string Outcome(string report) =>
            Files.Single(f => string.Equals(f.Report, report, StringComparison.Ordinal)).Outcome;

        public string? Detail(string report) =>
            Files.Single(f => string.Equals(f.Report, report, StringComparison.Ordinal)).Detail;

        public string? FileId(string report) =>
            Files.SingleOrDefault(f => string.Equals(f.Report, report, StringComparison.Ordinal))?.FileId;

        public string? FileName(string report) =>
            Files.SingleOrDefault(f => string.Equals(f.Report, report, StringComparison.Ordinal))?.FileName;

        public List<string> ReportsForCategory(string category) =>
            Files.Where(f => string.Equals(DriveCategory(f.Report), category, StringComparison.Ordinal))
                .Select(f => f.Report)
                .ToList();

        public int CategoryTotal(string category) =>
            ReportsForCategory(category).Count;

        private static string DriveCategory(string report)
        {
            int separator = report.IndexOf(':');
            return separator < 0 ? report : report[..separator];
        }
    }

    private static void SeedLibraryFolders(FakeDriveClient drive)
    {
        drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
        drive.SeedFolder("folder-seasons", "folder-mtshistory", "Seasons");
        drive.SeedFolder("folder-color-cup", "folder-mtshistory", "Color Cup");
        drive.SeedFolder("folder-color-cups", "folder-color-cup", "Cups");
        drive.SeedFolder("folder-color-teams", "folder-color-cup", "Teams Histories");
        drive.SeedFolder("folder-type-cup", "folder-mtshistory", "Type Cup");
        drive.SeedFolder("folder-type-cups", "folder-type-cup", "Cups");
        drive.SeedFolder("folder-type-teams", "folder-type-cup", "Teams Histories");
    }

    private static Dictionary<string, string> SnapshotContents(FakeDriveClient drive) =>
        drive.Files.Values
            .Where(f => !string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
            .ToDictionary(f => f.Id, f => f.Content, StringComparer.Ordinal);

    private static async Task AssertLibraryMatchesDownloadsAsync(HttpClient client, Guid saveId, FakeDriveClient drive)
    {
        using HttpResponseMessage status = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status").ConfigureAwait(false);
        using JsonDocument statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync().ConfigureAwait(false));
        int season = statusJson.RootElement.GetProperty("currentSeason").GetInt32();
        string seasonsId = drive.FoldersFor("folder-mtshistory", "Seasons").Single();
        string dossierId = drive.FileIn(seasonsId, $"season-{season}-dossier.md").Single();
        using HttpResponseMessage dossier = await client.GetAsync($"/api/saves/{saveId:D}/reports/season/{season}?format=md").ConfigureAwait(false);
        Normalize(await dossier.Content.ReadAsStringAsync().ConfigureAwait(false))
            .ShouldBe(Normalize(drive.Files[dossierId].Content));
    }

    private static string Normalize(string markdown)
    {
        StringBuilder builder = new();
        foreach (string line in markdown.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("Generated:", StringComparison.Ordinal))
            {
                builder.AppendLine(line.TrimEnd());
            }
        }

        return builder.ToString();
    }

    private static async Task<LibrarySyncResult> PostLibrarySyncAsync(HttpClient client, Guid saveId, bool allowCreateMissing = false)
    {
        string body = JsonSerializer.Serialize(new
        {
            confirmCrossSave = false,
            allowOverwriteEditedRemote = false,
            allowCreateMissing,
        });
        return await ParseLibraryResultAsync(await PostLibrarySyncRawAsync(client, saveId, body).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task<LibrarySyncResult> PostLibraryPreviewAsync(HttpClient client, Guid saveId, bool allowCreateMissing = false)
    {
        string body = JsonSerializer.Serialize(new
        {
            confirmCrossSave = false,
            allowOverwriteEditedRemote = false,
            allowCreateMissing,
        });
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/library/preview",
            new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return await ParseLibraryResultAsync(response.StatusCode, text).ConfigureAwait(false);
    }

    private static async Task<LibrarySyncResult> PostLibrarySyncSelectiveAsync(HttpClient client, Guid saveId, IReadOnlyList<string> onlyKeys)
    {
        string body = JsonSerializer.Serialize(new
        {
            confirmCrossSave = false,
            allowOverwriteEditedRemote = false,
            allowCreateMissing = false,
            categories = new[] { "color-edition", "color-team" },
            onlyKeys,
        });
        return await ParseLibraryResultAsync(await PostLibrarySyncRawAsync(client, saveId, body).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task<LibrarySyncResult> PostLibrarySyncMaxFilesAsync(HttpClient client, Guid saveId, int maxFiles)
    {
        string body = JsonSerializer.Serialize(new
        {
            confirmCrossSave = false,
            allowOverwriteEditedRemote = false,
            allowCreateMissing = true,
            maxFiles,
        });
        return await ParseLibraryResultAsync(await PostLibrarySyncRawAsync(client, saveId, body).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task<LibrarySyncResult> PostLibrarySyncSkipAsync(
        HttpClient client, Guid saveId, int skip, bool allowCreateMissing = false, int? maxFiles = null)
    {
        string body = JsonSerializer.Serialize(new
        {
            confirmCrossSave = false,
            allowOverwriteEditedRemote = false,
            allowCreateMissing,
            skip,
            maxFiles,
        });
        return await ParseLibraryResultAsync(await PostLibrarySyncRawAsync(client, saveId, body).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task<LibrarySyncResult> PostLibrarySyncSelectiveCreateAsync(
        HttpClient client, Guid saveId, IReadOnlyList<string> onlyKeys)
    {
        string body = JsonSerializer.Serialize(new
        {
            confirmCrossSave = false,
            allowOverwriteEditedRemote = false,
            allowCreateMissing = true,
            onlyKeys,
        });
        return await ParseLibraryResultAsync(await PostLibrarySyncRawAsync(client, saveId, body).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static async Task<string?> GetLibraryPublishedUtcAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/saves/{saveId:D}/reports/drive/status").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (json.RootElement.TryGetProperty("lastLibraryPublishedUtc", out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static Task<HttpResponseMessage> PostLibrarySyncRawAsync(HttpClient client, Guid saveId, string json) =>
        client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/library/sync",
            new StringContent(json, Encoding.UTF8, "application/json"));

    private static Task<LibrarySyncResult> ParseLibraryResultAsync(HttpResponseMessage response) =>
        ParseLibraryResponseAsync(response);

    private static async Task<LibrarySyncResult> ParseLibraryResultAsync(HttpStatusCode status, string text)
    {
        using JsonDocument json = JsonDocument.Parse(text);
        return await Task.FromResult(BuildResult(status, json)).ConfigureAwait(false);
    }

    private static async Task<LibrarySyncResult> ParseLibraryResponseAsync(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(text);
        HttpStatusCode status = response.StatusCode;
        response.Dispose();
        return await Task.FromResult(BuildResult(status, json)).ConfigureAwait(false);
    }

    private static LibrarySyncResult BuildResult(HttpStatusCode status, JsonDocument json)
    {
        JsonElement root = json.RootElement;
        if (status != HttpStatusCode.OK)
        {
            return new LibrarySyncResult(status, false, false, 0, null, [], root.GetRawText());
        }

        List<LibrarySyncFile> files = root.GetProperty("files").EnumerateArray()
            .Select(f => new LibrarySyncFile(
                f.GetProperty("report").GetString()!,
                f.GetProperty("outcome").GetString()!,
                f.TryGetProperty("fileId", out JsonElement fileId) && fileId.ValueKind == JsonValueKind.String
                    ? fileId.GetString()
                    : null,
                f.TryGetProperty("fileName", out JsonElement fileName) && fileName.ValueKind == JsonValueKind.String
                    ? fileName.GetString()
                    : null,
                f.TryGetProperty("detail", out JsonElement detail) && detail.ValueKind == JsonValueKind.String
                    ? detail.GetString()
                    : null))
            .ToList();
        int? nextSkip = root.TryGetProperty("nextSkip", out JsonElement skipElement) &&
            skipElement.ValueKind == JsonValueKind.Number
            ? skipElement.GetInt32()
            : null;
        return new LibrarySyncResult(
            status,
            root.GetProperty("dryRun").GetBoolean(),
            root.GetProperty("truncated").GetBoolean(),
            root.GetProperty("totalItems").GetInt32(),
            nextSkip,
            files,
            root.GetRawText());
    }

    private static async Task<Dictionary<string, string>> PostVerifyAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/verify", null).ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return json.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("report").GetString()!,
                e => e.GetProperty("status").GetString()!,
                StringComparer.Ordinal);
    }

    private static async Task SeedConnectionAsync(
        WebApplicationFactory<Program> factory,
        string accessToken = "access-seed",
        long expiresInSeconds = 3600)
    {
        DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
        await store.SaveConnectionAsync(new DrivePublishingStore.ConnectionDocument(
            "refresh-1", null, DriveScopes.DriveFile, DateTimeOffset.UtcNow,
            accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds)), CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task PutDestinationAsync(HttpClient client, Guid saveId, string json)
    {
        using HttpResponseMessage response = await client.PutAsync(
            $"/api/saves/{saveId:D}/reports/drive/destination",
            new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed record AutoState(bool Enabled, string Status, int? RequestedStage, int? LastStage);

    private static async Task<AutoState> GetAutoStateAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        JsonElement root = json.RootElement;
        return new AutoState(
            root.GetProperty("autoPublishEnabled").GetBoolean(),
            root.GetProperty("autoPublishStatus").GetString()!,
            root.TryGetProperty("autoRequestedGlobalStage", out JsonElement requested) && requested.ValueKind == JsonValueKind.Number
                ? requested.GetInt32()
                : null,
            root.TryGetProperty("lastGlobalStage", out JsonElement last) && last.ValueKind == JsonValueKind.Number
                ? last.GetInt32()
                : null);
    }

    private static async Task PutAutoPublishAsync(HttpClient client, Guid saveId, bool enabled)
    {
        using HttpResponseMessage response = await client.PutAsync(
            $"/api/saves/{saveId:D}/reports/drive/auto-publish",
            new StringContent($"{{\"enabled\":{enabled.ToString().ToLowerInvariant()}}}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<int> GetCurrentSeasonAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return json.RootElement.GetProperty("currentSeason").GetInt32();
    }

    private static async Task WaitForAutoUpToDateAsync(HttpClient client, Guid saveId, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        AutoState latest = await GetAutoStateAsync(client, saveId).ConfigureAwait(false);
        while (!string.Equals(latest.Status, "UpToDate", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException($"Timed out waiting for auto-publish UpToDate; last was {latest.Status}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            latest = await GetAutoStateAsync(client, saveId).ConfigureAwait(false);
        }
    }

    private static async Task WaitForAutoStageAsync(HttpClient client, Guid saveId, int stage, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            AutoState latest = await GetAutoStateAsync(client, saveId).ConfigureAwait(false);
            if (string.Equals(latest.Status, "UpToDate", StringComparison.Ordinal) && latest.LastStage == stage)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException($"Timed out waiting for auto-published stage {stage}; last was {latest.Status} at {latest.LastStage}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
    }

    private static (WebApplicationFactory<Program> Factory, string Root, FakeOAuthClient OAuth, FakeDriveClient Drive) CreateFactory(
        bool configured = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-drive-lib-" + Guid.NewGuid().ToString("N"));
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
                    o.GoogleClientId = configured ? "test-client" : string.Empty;
                    o.GoogleClientSecret = configured ? "test-secret" : string.Empty;
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

        public Task<OAuthTokenResponse> ExchangeCodeAsync(
            string clientId,
            string clientSecret,
            string redirectUri,
            string code,
            string codeVerifier,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OAuthTokenResponse("access-initial", 3600, "refresh-1", DriveScopes.DriveFile));

        public Task<OAuthTokenResponse> RefreshAsync(
            string clientId,
            string clientSecret,
            string refreshToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OAuthTokenResponse("access-refreshed", 3600, refreshToken, DriveScopes.DriveFile));

        public Task<bool> TryRevokeAsync(string token, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
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
            Files.Values
                .Where(f => !f.Trashed
                    && string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)
                    && f.Parents.Contains(parentId, StringComparer.Ordinal)
                    && string.Equals(f.Name, name, StringComparison.Ordinal))
                .Select(f => f.Id)
                .ToList();

        public IReadOnlyList<string> FileIn(string parentId, string? name) =>
            Files.Values
                .Where(f => !f.Trashed
                    && f.Parents.Contains(parentId, StringComparer.Ordinal)
                    && (name is null || string.Equals(f.Name, name, StringComparison.Ordinal)))
                .Select(f => f.Id)
                .ToList();

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
            if (file is not null
                && string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
            {
                throw new DriveApiException(HttpStatusCode.Forbidden, "Google Drive failed (403). fileNotDownloadable: Folders are not downloadable.");
            }

            return Task.FromResult(file?.Content);
        }

        public Task<IReadOnlyList<DriveFile>> ListByNameInFolderAsync(
            string accessToken,
            string folderId,
            string name,
            CancellationToken cancellationToken = default)
        {
            CheckDenied(folderId);
            IReadOnlyList<DriveFile> matches = Files.Values
                .Where(f => !f.Trashed
                    && f.Parents.Contains(folderId, StringComparer.Ordinal)
                    && string.Equals(f.Name, name, StringComparison.Ordinal))
                .Select(f => f.ToDriveFile())
                .ToList();
            return Task.FromResult(matches);
        }

        public Task<DriveFile?> FindFolderAsync(
            string accessToken,
            string parentFolderId,
            string name,
            CancellationToken cancellationToken = default)
        {
            CheckDenied(parentFolderId);
            List<DriveFile> folders = Files.Values
                .Where(f => !f.Trashed
                    && string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal)
                    && f.Parents.Contains(parentFolderId, StringComparer.Ordinal)
                    && string.Equals(f.Name, name, StringComparison.Ordinal))
                .Select(f => f.ToDriveFile())
                .ToList();
            return folders.Count switch
            {
                0 => Task.FromResult<DriveFile?>(null),
                1 => Task.FromResult<DriveFile?>(folders[0]),
                _ => throw new DriveConflictException("ambiguous"),
            };
        }

        public Task<DriveFile> CreateFolderAsync(
            string accessToken,
            string parentFolderId,
            string name,
            CancellationToken cancellationToken = default)
        {
            CheckDenied(parentFolderId);
            string id = $"folder-{++_nextId}";
            FakeFile file = new(id, name, "application/vnd.google-apps.folder", [parentFolderId], string.Empty, Stamp(), false, $"https://drive.test/file/{id}");
            Files[id] = file;
            return Task.FromResult(file.ToDriveFile());
        }

        public Task<DriveFile> CreateMarkdownAsync(
            string accessToken,
            string folderId,
            string name,
            string content,
            CancellationToken cancellationToken = default)
        {
            CheckDenied(folderId);
            string id = $"file-{++_nextId}";
            FakeFile file = new(id, name, "text/markdown", [folderId], content, Stamp(), false, $"https://drive.test/file/{id}");
            Files[id] = file;
            return Task.FromResult(file.ToDriveFile());
        }

        public Task<DriveFile> UpdateContentAsync(
            string accessToken,
            string fileId,
            string content,
            string? newName,
            CancellationToken cancellationToken = default)
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

        public sealed record FakeFile(
            string Id,
            string Name,
            string MimeType,
            List<string> Parents,
            string Content,
            string ModifiedTime,
            bool Trashed,
            string WebViewLink)
        {
            public DriveFile ToDriveFile() =>
                new(Id, Name, MimeType, Parents, ModifiedTime, null, Trashed, WebViewLink);
        }
    }
}
