using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

public sealed class GoogleDrivePublishingTests
{
    [Fact]
    public async Task ConnectFlow_LinksAccountAndReportsStatus()
    {
        var (factory, root, oauth, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await AssertNotConnectedAsync(client, saveId);
            Guid linked = await ConnectOverHttpAsync(factory, client, saveId);
            linked.ShouldBe(saveId);
            _ = drive;
            _ = oauth;
            using HttpResponseMessage status = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status");
            status.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("connected").GetBoolean().ShouldBeTrue();
            json.RootElement.GetProperty("configured").GetBoolean().ShouldBeTrue();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Publish_CreatesBothFilesMatchingDownloads()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            published.Outcomes.ShouldBe(["dossier:created", "almanac:created"]);
            await AssertStoredMatchesDownloadAsync(client, saveId, drive);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Republish_Unchanged_SkipsWritesWithoutDuplicates()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            _ = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            int filesAfterFirst = drive.Files.Count;
            filesAfterFirst.ShouldBe(3);
            PublishResult second = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            second.Outcomes.ShouldBe(["dossier:unchanged", "almanac:unchanged"]);
            drive.Files.Count.ShouldBe(filesAfterFirst);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdvanceStage_Republish_UpdatesInPlaceWithSameIds()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult first = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            PublishResult second = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            second.Status.ShouldBe(HttpStatusCode.OK);
            second.Outcomes.ShouldBe(["dossier:updated", "almanac:updated"]);
            second.FileId("dossier").ShouldBe(first.FileId("dossier"));
            second.FileId("almanac").ShouldBe(first.FileId("almanac"));
            drive.Files.Count.ShouldBe(3);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdoptedCustomNames_UpdateInPlaceWithoutExtraFiles()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-seasons", "folder-mtshistory", "Seasons");
            drive.SeedFile("dossier-adopted", "folder-seasons", "season-old.md", "# old dossier");
            drive.SeedFile("almanac-adopted", "folder-mtshistory", "universe-almanac-newtest.md", "# old almanac");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"dossier-adopted","dossierFileName":"season-17-dossier.md","almanacFileId":"almanac-adopted","almanacFileName":"universe-almanac-newtest.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Outcomes.ShouldBe(["dossier:updated", "almanac:updated"]);
            published.FileId("dossier").ShouldBe("dossier-adopted");
            published.FileId("almanac").ShouldBe("almanac-adopted");
            drive.Files["dossier-adopted"].Content.ShouldContain("# Season dossier");
            drive.Files["almanac-adopted"].Content.ShouldContain("# Universe almanac");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AmbiguousNames_StopWithAdoptionGuidance()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            _ = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            int baseline = drive.Files.Count;
            // Plant two same-named candidates in the Seasons folder, then clear
            // the adopted dossier id so the next publish takes the create path.
            string seasonsId = drive.FoldersFor("folder-mtshistory", "Seasons").Single();
            drive.SeedFile("dup-1", seasonsId, "clash.md", "a");
            drive.SeedFile("dup-2", seasonsId, "clash.md", "b");
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileName":"clash.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Outcome("dossier").ShouldBe("failed");
            published.Detail("dossier")!.ShouldContain("Adopt exactly one file");
            drive.Files.Count.ShouldBe(baseline + 2);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StaleRemoteEdit_NeedsExplicitOverwrite()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult first = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            string dossierId = first.FileId("dossier")!;
            drive.Files[dossierId] = drive.Files[dossierId] with { Content = "# hand-edited by user\n" };
            PublishResult blocked = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            blocked.Outcome("dossier").ShouldBe("failed");
            blocked.Detail("dossier")!.ShouldContain("edited independently");
            drive.Files[dossierId].Content.ShouldBe("# hand-edited by user\n");
            PublishResult forced = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: true);
            forced.Outcome("dossier").ShouldBe("updated");
            drive.Files[dossierId].Content.ShouldContain("# Season dossier");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedUpload_IsRecoverableAndSportingStateUnchanged()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult first = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            (long rng, long stream, _) = await ReadRngAndStandingsAsync(root, saveId);
            drive.FailUpdatesFor.Add(first.FileId("dossier")!);
            PublishResult failed = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            failed.Outcome("dossier").ShouldBe("failed");
            // The failed publish must not touch simulation state at all.
            (long rng2, long stream2, int standings2) = await ReadRngAndStandingsAsync(root, saveId);
            rng2.ShouldBe(rng);
            stream2.ShouldBe(stream);
            _ = standings2;
            drive.FailUpdatesFor.Clear();
            PublishResult retried = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            retried.Outcome("dossier").ShouldBe("updated");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CrossSaveReuse_NeedsExplicitConfirmation()
    {
        var (factory, root, _, _) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            await store.SaveDestinationAsync(new DrivePublishingStore.DestinationDocument(
                Guid.NewGuid(), "Other save", "folder-shared", "mtshistory", null,
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null), CancellationToken.None);
            using HttpResponseMessage conflict = await PutJsonAsync(client, $"/api/saves/{saveId:D}/reports/drive/destination",
                """{"mtshistoryFolderId":"folder-shared"}""");
            conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            using HttpResponseMessage accepted = await PutJsonAsync(client, $"/api/saves/{saveId:D}/reports/drive/destination",
                """{"mtshistoryFolderId":"folder-shared","confirmCrossSave":true}""");
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
            PublishResult blocked = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            blocked.Status.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingConfigOrAuth_FailsGracefully()
    {
        var (factory, root, _, _) = CreateFactory(configured: false);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient noRedirect = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using HttpResponseMessage authUrl = await noRedirect.GetAsync($"/api/saves/{saveId:D}/reports/drive/auth-url");
            authUrl.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            using HttpResponseMessage publish = await noRedirect.PostAsync(
                $"/api/saves/{saveId:D}/reports/drive/publish",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            publish.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Disconnect_ClearsConnectionAndBlocksPublish()
    {
        var (factory, root, oauth, _) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            using HttpResponseMessage disconnect = await client.PostAsync(
                $"/api/saves/{saveId:D}/reports/drive/disconnect", null);
            disconnect.StatusCode.ShouldBe(HttpStatusCode.OK);
            oauth.Revoked.Count.ShouldBeGreaterThanOrEqualTo(1);
            PublishResult blocked = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            blocked.Status.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExpiredAccessToken_RefreshesAndSucceeds()
    {
        var (factory, root, oauth, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory, accessToken: "stale-access", expiresInSeconds: -60);
            drive.OnlyAcceptTokens.Add("access-1");
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            published.Status.ShouldBe(HttpStatusCode.OK);
            oauth.RefreshCalls.ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdoptedFileOutsideDestination_IsRejectedWithoutOverwrite()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFile("outside-1", "other-folder", "season-99-dossier.md", "# unrelated");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"outside-1","dossierFileName":"season-99-dossier.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Status.ShouldBe(HttpStatusCode.OK);
            published.Outcome("dossier").ShouldBe("failed");
            published.Detail("dossier")!.ShouldContain("outside the configured destination");
            // Unrelated file must not be overwritten or renamed.
            drive.Files["outside-1"].Content.ShouldBe("# unrelated");
            drive.Files["outside-1"].Name.ShouldBe("season-99-dossier.md");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdoptedFolderId_IsRejectedWithoutOverwrite()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-as-file", "folder-mtshistory", "not-a-file");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"folder-as-file","dossierFileName":"season-1-dossier.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Outcome("dossier").ShouldBe("failed");
            published.Detail("dossier")!.ShouldContain("is a Drive folder");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeniedGrant_PublishFailsWithGrantGuidance()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFile("granted-no", "folder-seasons", "season-x-dossier.md", "# old");
            drive.DeniedIds.Add("granted-no");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"granted-no","dossierFileName":"season-x-dossier.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Outcome("dossier").ShouldBe("failed");
            published.Detail("dossier")!.ShouldContain("403");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StaleLegacyDossier_CreatesNewFilePreservingPrior()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult first = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            first.Status.ShouldBe(HttpStatusCode.OK);
            string dossierIdA = first.FileId("dossier")!;
            string contentA = drive.Files[dossierIdA].Content;
            int currentSeason = await ReadCurrentSeasonAsync(client, saveId);

            // Simulate a prior-season legacy mapping: the stored id belongs to
            // season N-1 while the save is at season N. Rename the Drive file
            // to the old stable name so the next publish must create a fresh
            // stable file and preserve the old Drive file.
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? loaded =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            loaded.ShouldNotBeNull();
            string oldName = $"season-{currentSeason - 1}-dossier.md";
            drive.Files[dossierIdA] = drive.Files[dossierIdA] with { Name = oldName };
            DrivePublishingStore.DestinationDocument stale = loaded! with
            {
                DossierFileId = dossierIdA,
                DossierFileName = oldName,
                LastSeasonNumber = currentSeason - 1,
                DossierBySeason = null,
            };
            await store.SaveDestinationAsync(stale, CancellationToken.None);

            PublishResult second = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            second.Status.ShouldBe(HttpStatusCode.OK);
            second.Outcome("dossier").ShouldBe("created");
            string dossierIdB = second.FileId("dossier")!;
            dossierIdB.Equals(dossierIdA, StringComparison.Ordinal).ShouldBeFalse();
            // Prior season file is preserved byte-for-byte.
            drive.Files[dossierIdA].Content.ShouldBe(contentA);
            drive.Files.ShouldContainKey(dossierIdB);
            DrivePublishingStore.DestinationDocument? after =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            after.ShouldNotBeNull();
            after!.DossierBySeason.ShouldNotBeNull();
            after.DossierBySeason!.ShouldContainKey(currentSeason - 1);
            after.DossierBySeason.ShouldContainKey(currentSeason);
            after.DossierBySeason[currentSeason - 1].FileId.ShouldBe(dossierIdA);
            after.DossierBySeason[currentSeason].FileId.ShouldBe(dossierIdB);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DossierHistory_ResolvesPerSeasonWithoutReuse()
    {
        Guid saveId = Guid.NewGuid();
        var existing = new DrivePublishingStore.DestinationDocument(
            saveId, "Save", "folder-mtshistory", "mtshistory", "folder-seasons",
            "old-id", "season-1-dossier.md", null, null, "fp-old", null, "t-old", null,
            DateTimeOffset.UtcNow, 1, "Phase", 0, "updated", null, null,
            new Dictionary<int, DrivePublishingStore.DossierSlot>
            {
                [1] = new DrivePublishingStore.DossierSlot("old-id", "season-1-dossier.md", "fp-old", "t-old"),
            });
        (string? id, string name) = DrivePublishingStore.ResolveDossierForSeason(existing, 2, "season-2-dossier.md");
        id.ShouldBeNull();
        name.ShouldBe("season-2-dossier.md");

        (string? curId, string? curName, _, _) = DrivePublishingStore.CurrentDossierView(existing, 2);
        curId.ShouldBeNull();
        curName.ShouldBeNull();

        (string? curId1, string? curName1, _, _) = DrivePublishingStore.CurrentDossierView(existing, 1);
        curId1.ShouldBe("old-id");
        curName1.ShouldBe("season-1-dossier.md");
    }

    [Fact]
    public async Task VerifyEndpoint_ReportsGrantAndLocation()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            drive.SeedFolder("folder-seasons", "folder-mtshistory", "Seasons");
            drive.SeedFile("outside-v", "other-folder", "odd.md", "# odd");
            drive.SeedFile("denied-v", "folder-mtshistory", "denied.md", "# denied");
            drive.DeniedIds.Add("denied-v");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"outside-v","dossierFileName":"odd.md","almanacFileId":"denied-v","almanacFileName":"denied.md"}""");
            using HttpResponseMessage response = await client.PostAsync(
                $"/api/saves/{saveId:D}/reports/drive/verify", null);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Dictionary<string, string> byReport = json.RootElement.GetProperty("checks").EnumerateArray()
                .ToDictionary(
                    e => e.GetProperty("report").GetString()!,
                    e => e.GetProperty("status").GetString()!,
                    StringComparer.Ordinal);
            byReport["dossier"].ShouldBe("outside-destination");
            byReport["almanac"].ShouldBe("needs-grant");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdoptedMissingId_FailsClosedWithoutDuplicate()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            // Adopt ids that do not exist in Drive (deleted or never granted:
            // drive.file hides ungranted files as 404). Publishing must fail
            // closed, not create same-named replacements.
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"missing-dossier","dossierFileName":"season-1-dossier.md","almanacFileId":"missing-almanac","almanacFileName":"universe-almanac.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Status.ShouldBe(HttpStatusCode.OK);
            published.Outcome("dossier").ShouldBe("failed");
            published.Outcome("almanac").ShouldBe("failed");
            published.Detail("dossier")!.ShouldContain("not found");
            published.Detail("dossier")!.ShouldContain("did not create a duplicate");
            published.Detail("almanac")!.ShouldContain("not found");
            // No Markdown files may be created for the missing adopted ids;
            // only the auto-resolved Seasons/ folder may exist.
            drive.Files.Values
                .Where(f => !string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
                .ShouldBeEmpty();
            drive.Files.ShouldNotContainKey("missing-dossier");
            drive.Files.ShouldNotContainKey("missing-almanac");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdoptedDossier_InfersOriginalSeasonsWithoutDuplicate()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            drive.SeedFolder("folder-seasons-orig", "folder-mtshistory", "Seasons");
            drive.SeedFile("dossier-orig", "folder-seasons-orig", "season-1-dossier.md", "# old dossier");
            drive.SeedFile("almanac-orig", "folder-mtshistory", "universe-almanac-newtest.md", "# old almanac");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            // No explicit Seasons folder id: publishing must infer the adopted
            // dossier's parent instead of creating a duplicate Seasons/ folder.
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"dossier-orig","dossierFileName":"season-1-dossier.md","almanacFileId":"almanac-orig","almanacFileName":"universe-almanac-newtest.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Status.ShouldBe(HttpStatusCode.OK);
            published.Outcome("dossier").ShouldBe("updated");
            published.FileId("dossier").ShouldBe("dossier-orig");
            // Exactly one Seasons/ folder under mtshistory: no duplicate.
            drive.FoldersFor("folder-mtshistory", "Seasons").Count.ShouldBe(1);
            drive.FoldersFor("folder-mtshistory", "Seasons").Single().ShouldBe("folder-seasons-orig");
            // The inferred Seasons folder is persisted for later publishes.
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? after =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            after.ShouldNotBeNull();
            after!.SeasonsFolderId.ShouldBe("folder-seasons-orig");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdoptedMissingId_DoesNotCreateSeasonsFolder()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"missing-dossier","dossierFileName":"season-1-dossier.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Status.ShouldBe(HttpStatusCode.OK);
            published.Outcome("dossier").ShouldBe("failed");
            // Fail closed without creating a duplicate Seasons/ folder.
            drive.FoldersFor("folder-mtshistory", "Seasons").ShouldBeEmpty();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitSeasonsFolderOutsideDestination_IsRejectedWithoutCreation()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            drive.SeedFolder("folder-elsewhere", "root", "other");
            drive.SeedFolder("folder-seasons-wrong", "folder-elsewhere", "Seasons");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","seasonsFolderId":"folder-seasons-wrong"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Status.ShouldBe(HttpStatusCode.Conflict);
            // No Seasons/ folder may be created inside the destination.
            drive.FoldersFor("folder-mtshistory", "Seasons").ShouldBeEmpty();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingSeasonsWithoutCreateConfirmation_FailsClosedWithoutDuplicate()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            // No Seasons folder, no adopted dossier, no creation confirmation:
            // with drive.file an empty listing does not prove the name is free,
            // so publishing must fail closed instead of creating a duplicate.
            PublishResult blocked = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: false);
            blocked.Status.ShouldBe(HttpStatusCode.Conflict);
            blocked.Body.ShouldContain("does not prove the name is free");
            drive.FoldersFor("folder-mtshistory", "Seasons").ShouldBeEmpty();
            // Explicit creation confirmation allows the first publish.
            PublishResult created = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            created.Status.ShouldBe(HttpStatusCode.OK);
            created.Outcome("dossier").ShouldBe("created");
            drive.FoldersFor("folder-mtshistory", "Seasons").Count.ShouldBe(1);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFilesWithoutCreateConfirmation_FailClosedWithoutDuplicate()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            drive.SeedFolder("folder-seasons", "folder-mtshistory", "Seasons");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","seasonsFolderId":"folder-seasons"}""");
            PublishResult blocked = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: false);
            blocked.Status.ShouldBe(HttpStatusCode.OK);
            blocked.Outcome("dossier").ShouldBe("failed");
            blocked.Outcome("almanac").ShouldBe("failed");
            blocked.Detail("dossier")!.ShouldContain("does not prove the name is free");
            blocked.Detail("almanac")!.ShouldContain("does not prove the name is free");
            drive.Files.Values
                .Where(f => !string.Equals(f.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
                .ShouldBeEmpty();
            PublishResult created = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            created.Outcomes.ShouldBe(["dossier:created", "almanac:created"]);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CrossOriginDriveRequests_AreBlocked()
    {
        var (factory, root, _, _) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            using HttpRequestMessage evilPublish = new(HttpMethod.Post, $"/api/saves/{saveId:D}/reports/drive/publish");
            evilPublish.Headers.Add("Origin", "https://evil.example");
            evilPublish.Content = new StringContent("""{"confirmCrossSave":false,"allowOverwriteEditedRemote":false,"allowCreateMissing":true}""", Encoding.UTF8, "application/json");
            using HttpResponseMessage blockedPublish = await client.SendAsync(evilPublish);
            blockedPublish.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using HttpRequestMessage evilDisconnect = new(HttpMethod.Post, $"/api/saves/{saveId:D}/reports/drive/disconnect");
            evilDisconnect.Headers.Add("Origin", "https://evil.example");
            using HttpResponseMessage blockedDisconnect = await client.SendAsync(evilDisconnect);
            blockedDisconnect.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            using HttpRequestMessage evilDestination = new(HttpMethod.Put, $"/api/saves/{saveId:D}/reports/drive/destination");
            evilDestination.Headers.Add("Origin", "https://evil.example");
            evilDestination.Content = new StringContent("""{"mtshistoryFolderId":"folder-mtshistory"}""", Encoding.UTF8, "application/json");
            using HttpResponseMessage blockedDestination = await client.SendAsync(evilDestination);
            blockedDestination.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            // Same-origin (no Origin header, like curl/tests) still works.
            using HttpResponseMessage ok = await client.PostAsync($"/api/saves/{saveId:D}/reports/drive/verify", null);
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CrossOriginDifferentLoopbackPort_IsBlocked()
    {
        var (factory, root, _, _) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            // Both ends are loopback literals, but different ports are
            // different origins and must be rejected.
            using HttpRequestMessage crossPort = new(HttpMethod.Post, $"/api/saves/{saveId:D}/reports/drive/publish");
            string host = client.BaseAddress?.Host ?? "localhost";
            crossPort.Headers.Add("Origin", $"http://{host}:5174");
            crossPort.Content = new StringContent("""{"confirmCrossSave":false,"allowOverwriteEditedRemote":false,"allowCreateMissing":true}""", Encoding.UTF8, "application/json");
            using HttpResponseMessage blocked = await client.SendAsync(crossPort);
            blocked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentFirstPublishes_DoNotDuplicateHierarchy()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            // Two parallel first publishes race the same list-then-create
            // sequences. The per-save publish lock must serialize them so
            // only one Seasons/ folder and one file per report exist.
            Task<PublishResult> first = PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            Task<PublishResult> second = PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            PublishResult[] results = await Task.WhenAll(first, second);
            results[0].Status.ShouldBe(HttpStatusCode.OK);
            results[1].Status.ShouldBe(HttpStatusCode.OK);
            drive.FoldersFor("folder-mtshistory", "Seasons").Count.ShouldBe(1);
            string seasonsId = drive.FoldersFor("folder-mtshistory", "Seasons").Single();
            drive.FileIn(seasonsId, null).Count.ShouldBe(1);
            drive.FileIn("folder-mtshistory", null)
                .Where(id => !string.Equals(drive.Files[id].MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
                .Count()
                .ShouldBe(1);
            drive.Files.Count.ShouldBe(3);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdoptedIdWithoutExplicitName_PreservesRemoteName()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            drive.SeedFolder("folder-seasons", "folder-mtshistory", "Seasons");
            drive.SeedFile("dossier-adopted", "folder-seasons", "season-1-dossier.md", "# old dossier");
            drive.SeedFile("almanac-adopted", "folder-mtshistory", "universe-almanac-newtest.md", "# old almanac");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            // Adopt by id with no explicit file names: publishing must keep
            // the remote custom names instead of renaming to defaults.
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"dossier-adopted","almanacFileId":"almanac-adopted"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            published.Status.ShouldBe(HttpStatusCode.OK);
            published.Outcome("dossier").ShouldBe("updated");
            published.Outcome("almanac").ShouldBe("updated");
            published.FileId("dossier").ShouldBe("dossier-adopted");
            published.FileId("almanac").ShouldBe("almanac-adopted");
            drive.Files["dossier-adopted"].Name.ShouldBe("season-1-dossier.md");
            drive.Files["almanac-adopted"].Name.ShouldBe("universe-almanac-newtest.md");
            drive.Files["almanac-adopted"].Content.ShouldContain("# Universe almanac");
            // Second publish with no sporting change is idempotent.
            PublishResult second = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            second.Outcomes.ShouldBe(["dossier:unchanged", "almanac:unchanged"]);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RenameWithUnchangedContent_RenamesInPlaceWithSameIds()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult first = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            first.Outcomes.ShouldBe(["dossier:created", "almanac:created"]);
            string dossierId = first.FileId("dossier")!;
            string almanacId = first.FileId("almanac")!;
            PublishResult second = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            second.Outcomes.ShouldBe(["dossier:unchanged", "almanac:unchanged"]);

            // User explicitly renames both adopted reports without any
            // sporting change: the next publish must rename in place with
            // the same file ids instead of reporting unchanged.
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? before =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            before.ShouldNotBeNull();
            string seasonsId = before!.SeasonsFolderId!;
            string putJson = "{\"mtshistoryFolderId\":\"folder-mtshistory\",\"seasonsFolderId\":\"" + seasonsId
                + "\",\"dossierFileId\":\"" + dossierId + "\",\"dossierFileName\":\"season-1-renamed.md\""
                + ",\"almanacFileId\":\"" + almanacId + "\",\"almanacFileName\":\"universe-almanac-renamed.md\"}";
            await PutDestinationAsync(client, saveId, putJson);

            PublishResult third = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            third.Outcome("dossier").ShouldBe("updated");
            third.Outcome("almanac").ShouldBe("updated");
            third.FileId("dossier").ShouldBe(dossierId);
            third.FileId("almanac").ShouldBe(almanacId);
            drive.Files[dossierId].Name.ShouldBe("season-1-renamed.md");
            drive.Files[almanacId].Name.ShouldBe("universe-almanac-renamed.md");
            drive.Files.Count.ShouldBe(3);

            PublishResult fourth = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            fourth.Outcomes.ShouldBe(["dossier:unchanged", "almanac:unchanged"]);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RenameWithUnchangedContent_TimeoutWithoutRename_FailsClosed()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult first = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            first.Outcomes.ShouldBe(["dossier:created", "almanac:created"]);
            string dossierId = first.FileId("dossier")!;
            string almanacId = first.FileId("almanac")!;
            string originalDossierName = drive.Files[dossierId].Name;
            string originalAlmanacName = drive.Files[almanacId].Name;

            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? before =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            before.ShouldNotBeNull();
            string seasonsId = before!.SeasonsFolderId!;
            string putJson = "{\"mtshistoryFolderId\":\"folder-mtshistory\",\"seasonsFolderId\":\"" + seasonsId
                + "\",\"dossierFileId\":\"" + dossierId + "\",\"dossierFileName\":\"season-1-renamed.md\""
                + ",\"almanacFileId\":\"" + almanacId + "\",\"almanacFileName\":\"universe-almanac-renamed.md\"}";
            await PutDestinationAsync(client, saveId, putJson);

            // Simulate a timeout where the rename never lands: content still
            // matches, but the remote name is unchanged. This must fail
            // closed, not report updated with an unperformed name.
            drive.FailUpdatesFor.Add(dossierId);
            drive.FailUpdatesFor.Add(almanacId);
            PublishResult third = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            third.Outcomes.ShouldBe(["dossier:failed", "almanac:failed"]);
            third.Detail("dossier")!.ShouldContain("could not be confirmed");
            drive.Files[dossierId].Name.ShouldBe(originalDossierName);
            drive.Files[almanacId].Name.ShouldBe(originalAlmanacName);

            // Retry after the transient failure renames in place.
            drive.FailUpdatesFor.Clear();
            PublishResult fourth = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false);
            fourth.Outcomes.ShouldBe(["dossier:updated", "almanac:updated"]);
            drive.Files[dossierId].Name.ShouldBe("season-1-renamed.md");
            drive.Files[almanacId].Name.ShouldBe("universe-almanac-renamed.md");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task VerifyFolder_AccessibleFolderReportsOkDespiteDownloadForbidden()
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
                $"/api/saves/{saveId:D}/reports/drive/verify", null);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Dictionary<string, string> byReport = json.RootElement.GetProperty("checks").EnumerateArray()
                .ToDictionary(
                    e => e.GetProperty("report").GetString()!,
                    e => e.GetProperty("status").GetString()!,
                    StringComparer.Ordinal);
            // The fake Drive now returns 403 fileNotDownloadable for folder
            // media downloads (like real Drive). Verification must use a list
            // operation, so a granted folder still reports ok.
            byReport["mtshistory"].ShouldBe("ok");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Status_ExposesPickerConfigWithoutSecrets()
    {
        var (factory, root, _, _) = CreateFactory(configured: true, pickerApiKey: "picker-test-key", pickerAppId: "1234567890");
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement rootElement = json.RootElement;
            rootElement.GetProperty("pickerEnabled").GetBoolean().ShouldBeTrue();
            rootElement.GetProperty("pickerClientId").GetString().ShouldBe("test-client");
            rootElement.GetProperty("pickerApiKey").GetString().ShouldBe("picker-test-key");
            rootElement.GetProperty("pickerAppId").GetString().ShouldBe("1234567890");
            // Secrets and tokens must never be exposed to the frontend.
            rootElement.TryGetProperty("googleClientSecret", out _).ShouldBeFalse();
            rootElement.TryGetProperty("refreshToken", out _).ShouldBeFalse();
            rootElement.TryGetProperty("accessToken", out _).ShouldBeFalse();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Status_PickerDisabledWithoutApiKey()
    {
        var (factory, root, _, _) = CreateFactory(configured: true, pickerApiKey: "", pickerAppId: "1234567890");
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("pickerEnabled").GetBoolean().ShouldBeFalse();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Status_PickerDisabledWithoutAppId()
    {
        var (factory, root, _, _) = CreateFactory(configured: true, pickerApiKey: "picker-test-key", pickerAppId: "");
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("pickerEnabled").GetBoolean().ShouldBeFalse();
            // App id must not leak as empty string; missing config is null.
            json.RootElement.GetProperty("pickerAppId").ValueKind.ShouldBe(JsonValueKind.Null);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PendingState_TraversalIsRejectedWithoutDeletingPending()
    {
        var (factory, root, _, _) = CreateFactory();
        try
        {
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            string validState = new('A', 43);
            Guid saveId = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await store.SavePendingAsync(
                new DrivePublishingStore.PendingAuthDocument(validState, saveId, "verifier", now, now.AddMinutes(10)),
                CancellationToken.None);

            // Attacker-controlled callback states must fail closed before any
            // filesystem path is touched and must not delete the real pending.
            // Blank states throw (fail closed with 400); non-canonical states
            // return null (invalid/expired with 400) without touching disk.
            await Should.ThrowAsync<ArgumentException>(store.TakePendingAsync("", CancellationToken.None));
            await Should.ThrowAsync<ArgumentException>(store.TakePendingAsync("   ", CancellationToken.None));
            string[] traversalStates =
            [
                "../connection",
                "..%2Fconnection",
                "../../destinations/abc",
                "a/b",
                "a\\b",
                "short",
                new string('B', 42),
                new string('C', 44),
                "../" + validState,
                validState + "/x",
            ];
            foreach (string bad in traversalStates)
            {
                DrivePublishingStore.PendingAuthDocument? rejected =
                    await store.TakePendingAsync(bad, CancellationToken.None);
                rejected.ShouldBeNull($"state '{bad}' must be rejected");
            }

            // The legitimate pending must still be consumable exactly once.
            DrivePublishingStore.PendingAuthDocument? pending =
                await store.TakePendingAsync(validState, CancellationToken.None);
            pending.ShouldNotBeNull();
            pending!.State.ShouldBe(validState);
            (await store.TakePendingAsync(validState, CancellationToken.None)).ShouldBeNull();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PendingState_CanonicalShapeIsEnforced()
    {
        DrivePublishingStore.IsCanonicalOAuthState(new string('a', 43)).ShouldBeTrue();
        DrivePublishingStore.IsCanonicalOAuthState(new string('Z', 43)).ShouldBeTrue();
        DrivePublishingStore.IsCanonicalOAuthState(new string('0', 43)).ShouldBeTrue();
        DrivePublishingStore.IsCanonicalOAuthState("AB-_ab09" + new string('x', 35)).ShouldBeTrue();
        DrivePublishingStore.IsCanonicalOAuthState("short").ShouldBeFalse();
        DrivePublishingStore.IsCanonicalOAuthState(new string('A', 42)).ShouldBeFalse();
        DrivePublishingStore.IsCanonicalOAuthState(new string('A', 44)).ShouldBeFalse();
        DrivePublishingStore.IsCanonicalOAuthState(new string('A', 42) + "/").ShouldBeFalse();
        DrivePublishingStore.IsCanonicalOAuthState(new string('A', 42) + ".").ShouldBeFalse();
        DrivePublishingStore.IsCanonicalOAuthState("../" + new string('A', 40)).ShouldBeFalse();
    }

    [Fact]
    public async Task AdoptedDossierInNonSeasonsChild_IsNotInferredAsSeasons()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            // Any other mtshistory child (e.g. a reserved Cup hierarchy folder)
            // must never be inferred as the Seasons/ folder.
            drive.SeedFolder("folder-cups", "folder-mtshistory", "Cups");
            drive.SeedFile("dossier-wrong", "folder-cups", "season-1-dossier.md", "# unrelated cups report");
            drive.SeedFile("almanac-ok", "folder-mtshistory", "universe-almanac.md", "# old almanac");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","dossierFileId":"dossier-wrong","dossierFileName":"season-1-dossier.md","almanacFileId":"almanac-ok","almanacFileName":"universe-almanac.md"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            published.Status.ShouldBe(HttpStatusCode.OK);
            published.Outcome("dossier").ShouldBe("failed");
            published.Detail("dossier")!.ShouldContain("outside the configured destination");
            // Unrelated file must not be overwritten.
            drive.Files["dossier-wrong"].Content.ShouldBe("# unrelated cups report");
            // No Seasons/ folder may be created and the wrong folder must not
            // be persisted as the Seasons folder id.
            drive.FoldersFor("folder-mtshistory", "Seasons").ShouldBeEmpty();
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? after =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            after.ShouldNotBeNull();
            after!.SeasonsFolderId.ShouldBeNull();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitSeasonsFolderWithWrongName_IsRejectedWithoutCreation()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            drive.SeedFolder("folder-cups", "folder-mtshistory", "Cups");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","seasonsFolderId":"folder-cups"}""");
            PublishResult published = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            published.Status.ShouldBe(HttpStatusCode.Conflict);
            published.Body.ShouldContain("not the 'Seasons' folder");
            drive.FoldersFor("folder-mtshistory", "Seasons").ShouldBeEmpty();
            using HttpResponseMessage verify = await client.PostAsync(
                $"/api/saves/{saveId:D}/reports/drive/verify", null);
            verify.StatusCode.ShouldBe(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await verify.Content.ReadAsStringAsync());
            Dictionary<string, string> byReport = json.RootElement.GetProperty("checks").EnumerateArray()
                .ToDictionary(
                    e => e.GetProperty("report").GetString()!,
                    e => e.GetProperty("status").GetString()!,
                    StringComparer.Ordinal);
            byReport["seasons"].ShouldBe("outside-destination");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentPublishAndDestinationPut_DoNotLosePut()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            drive.SeedFolder("folder-mtshistory", "root", "mtshistory");
            drive.SeedFolder("folder-seasons", "folder-mtshistory", "Seasons");
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId,
                """{"mtshistoryFolderId":"folder-mtshistory","seasonsFolderId":"folder-seasons"}""");
            PublishResult first = await PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: false, allowCreateMissing: true);
            first.Status.ShouldBe(HttpStatusCode.OK);

            // Advance sporting state so the next publish must update (not
            // report unchanged, which would never hit UpdateContent).
            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Second publish blocks inside UpdateContent; land a PUT
            // mid-publish. The shared gate must serialize them so the PUT wins.
            DrivePublishingStore storeBefore = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? before =
                await storeBefore.GetDestinationAsync(saveId, CancellationToken.None);
            before.ShouldNotBeNull();
            before!.AlmanacFileId.ShouldNotBeNull();
            string almanacId = before.AlmanacFileId!;
            string dossierId = first.FileId("dossier")!;
            string dossierName = drive.Files[dossierId].Name;
            drive.BlockUpdates = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PublishResult> publishTask = PostPublishAsync(client, saveId, confirmCrossSave: false, allowOverwrite: true, allowCreateMissing: true);
            // Wait until the publish is inside the Drive update.
            await drive.EnteredUpdate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // PUT runs while publish holds the gate: it must wait, then win.
            // Run it in the background so this test does not deadlock: the
            // PUT blocks on the shared gate until the publish unblocks.
            // Include the adopted ids so the PUT renames without clearing.
            string putJson = "{\"mtshistoryFolderId\":\"folder-mtshistory\",\"seasonsFolderId\":\"folder-seasons\",\"dossierFileId\":\"" + dossierId + "\",\"dossierFileName\":\"" + dossierName + "\",\"almanacFileId\":\"" + almanacId + "\",\"almanacFileName\":\"universe-almanac-renamed.md\"}";
            Task<HttpResponseMessage> putTask = PutJsonAsync(client, $"/api/saves/{saveId:D}/reports/drive/destination", putJson);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            drive.BlockUpdates.TrySetResult(null);
            using HttpResponseMessage put = await putTask;
            put.StatusCode.ShouldBe(HttpStatusCode.OK);
            PublishResult published = await publishTask;
            published.Status.ShouldBe(HttpStatusCode.OK);

            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? after =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            after.ShouldNotBeNull();
            after!.AlmanacFileName.ShouldBe("universe-almanac-renamed.md");
            after.AlmanacFileId.ShouldNotBeNull();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record PublishResult(
        HttpStatusCode Status,
        string[] Outcomes,
        string Body)
    {
        public string Outcome(string report) =>
            Outcomes.Single(o => o.StartsWith(report + ":", StringComparison.Ordinal)).Split(':')[1];

        public string? Detail(string report)
        {
            using JsonDocument json = JsonDocument.Parse(Body);
            foreach (JsonElement file in json.RootElement.GetProperty("files").EnumerateArray())
            {
                if (string.Equals(file.GetProperty("report").GetString(), report, StringComparison.Ordinal))
                {
                    return file.GetProperty("detail").GetString();
                }
            }

            return null;
        }

        public string? FileId(string report)
        {
            using JsonDocument json = JsonDocument.Parse(Body);
            foreach (JsonElement file in json.RootElement.GetProperty("files").EnumerateArray())
            {
                if (string.Equals(file.GetProperty("report").GetString(), report, StringComparison.Ordinal))
                {
                    return file.GetProperty("fileId").GetString();
                }
            }

            return null;
        }
    }

    private static async Task AssertNotConnectedAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage status = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status").ConfigureAwait(false);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await status.Content.ReadAsStringAsync().ConfigureAwait(false));
        json.RootElement.GetProperty("connected").GetBoolean().ShouldBeFalse();
    }

    private static async Task<Guid> ConnectOverHttpAsync(
        WebApplicationFactory<Program> factory,
        HttpClient client,
        Guid saveId)
    {
        using HttpResponseMessage auth = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/auth-url").ConfigureAwait(false);
        auth.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument authJson = JsonDocument.Parse(await auth.Content.ReadAsStringAsync().ConfigureAwait(false));
        string url = authJson.RootElement.GetProperty("authUrl").GetString()!;
        string state = ExtractQueryValue(url, "state");
        using HttpClient noRedirect = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using HttpResponseMessage callback = await noRedirect.GetAsync(
            $"/api/reports/drive/callback?code=valid-code&state={Uri.EscapeDataString(state)}").ConfigureAwait(false);
        callback.StatusCode.ShouldBe(HttpStatusCode.Found);
        string location = callback.Headers.Location!.ToString();
        location.ShouldContain($"/saves/{saveId:D}/reports?drive=connected");
        return saveId;
    }

    private static string ExtractQueryValue(string url, string key)
    {
        string query = new Uri(url).Query.TrimStart('?');
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        throw new InvalidOperationException($"Query value '{key}' not found.");
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
        using HttpResponseMessage response = await PutJsonAsync(client, $"/api/saves/{saveId:D}/reports/drive/destination", json).ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<int> ReadCurrentSeasonAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage status = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status").ConfigureAwait(false);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await status.Content.ReadAsStringAsync().ConfigureAwait(false));
        return json.RootElement.GetProperty("currentSeason").GetInt32();
    }

    private static Task<HttpResponseMessage> PutJsonAsync(HttpClient client, string url, string json) =>
        client.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<PublishResult> PostPublishAsync(
        HttpClient client,
        Guid saveId,
        bool confirmCrossSave,
        bool allowOverwrite,
        bool allowCreateMissing = false)
    {
        string body = JsonSerializer.Serialize(new { confirmCrossSave, allowOverwriteEditedRemote = allowOverwrite, allowCreateMissing });
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/publish",
            new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new PublishResult(response.StatusCode, [], text);
        }

        using JsonDocument json = JsonDocument.Parse(text);
        List<string> outcomes = json.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => $"{f.GetProperty("report").GetString()}:{f.GetProperty("outcome").GetString()}")
            .ToList();
        return new PublishResult(response.StatusCode, [.. outcomes], text);
    }

    private static async Task AssertStoredMatchesDownloadAsync(
        HttpClient client,
        Guid saveId,
        FakeDriveClient drive)
    {
        using HttpResponseMessage status = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status").ConfigureAwait(false);
        using JsonDocument statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync().ConfigureAwait(false));
        int season = statusJson.RootElement.GetProperty("currentSeason").GetInt32();
        string seasonsId = drive.FoldersFor("folder-mtshistory", "Seasons").Single();
        string dossierId = drive.FileIn(seasonsId, $"season-{season}-dossier.md").Single();
        string almanacId = drive.FileIn("folder-mtshistory", null).Single(id => drive.Files[id].Name.EndsWith(".md", StringComparison.Ordinal));
        using HttpResponseMessage dossier = await client.GetAsync($"/api/saves/{saveId:D}/reports/season/{season}?format=md").ConfigureAwait(false);
        using HttpResponseMessage almanac = await client.GetAsync($"/api/saves/{saveId:D}/reports/almanac?format=md").ConfigureAwait(false);
        Normalize(await dossier.Content.ReadAsStringAsync().ConfigureAwait(false)).ShouldBe(Normalize(drive.Files[dossierId].Content));
        Normalize(await almanac.Content.ReadAsStringAsync().ConfigureAwait(false)).ShouldBe(Normalize(drive.Files[almanacId].Content));
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

    private static async Task<(long Rng, long Stream, int Standings)> ReadRngAndStandingsAsync(string root, Guid saveId)
    {
        string path = Path.Combine(root, "saves", $"{saveId:N}.db");
        var options = new DbContextOptionsBuilder<SaveDbContext>().UseSqlite($"Data Source={path}").Options;
        using var context = new SaveDbContext(options);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        int standings = await context.SeasonStandings.AsNoTracking().CountAsync().ConfigureAwait(false);
        return (rng.State, rng.Stream, standings);
    }

    private static (WebApplicationFactory<Program> Factory, string Root, FakeOAuthClient OAuth, FakeDriveClient Drive) CreateFactory(
        bool configured = true,
        string pickerApiKey = "",
        string pickerAppId = "")
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-drive-" + Guid.NewGuid().ToString("N"));
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
                    o.GooglePickerApiKey = pickerApiKey;
                    o.GooglePickerAppId = pickerAppId;
                });
                services.AddSingleton<IGoogleOAuthClient>(oauth);
                services.AddSingleton<IGoogleDriveClient>(drive);
            }));
        return (factory, root, oauth, drive);
    }

    private sealed class FakeOAuthClient : IGoogleOAuthClient
    {
        public List<string> Revoked { get; } = [];

        public int RefreshCalls { get; private set; }

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
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            if (string.Equals(refreshToken, "bad-refresh", StringComparison.Ordinal))
            {
                throw new DriveNotConnectedException("expired");
            }

            return Task.FromResult(new OAuthTokenResponse($"access-{RefreshCalls}", 3600, refreshToken, DriveScopes.DriveFile));
        }

        public Task<bool> TryRevokeAsync(string token, CancellationToken cancellationToken = default)
        {
            Revoked.Add(token);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDriveClient : IGoogleDriveClient
    {
        public readonly Dictionary<string, FakeFile> Files = new(StringComparer.Ordinal);

        public readonly HashSet<string> FailUpdatesFor = new(StringComparer.Ordinal);

        public readonly HashSet<string> OnlyAcceptTokens = new(StringComparer.Ordinal);

        public readonly HashSet<string> DeniedIds = new(StringComparer.Ordinal);

        public bool FailNextCreate { get; set; }

        public TaskCompletionSource<object?>? BlockUpdates { get; set; }

        public TaskCompletionSource<object?> EnteredUpdate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            CheckToken(accessToken);
            CheckDenied(fileId);
            Files.TryGetValue(fileId, out FakeFile? file);
            return Task.FromResult<DriveFile?>(file?.ToDriveFile());
        }

        public Task<string?> DownloadFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default)
        {
            CheckToken(accessToken);
            CheckDenied(fileId);
            Files.TryGetValue(fileId, out FakeFile? file);
            if (file is not null
                && string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
            {
                // Mirror real Drive: folders are not downloadable media and
                // return 403 fileNotDownloadable even when the folder itself is
                // accessible. Folder verification must use list, not download.
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
            CheckToken(accessToken);
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
            CheckToken(accessToken);
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
            CheckToken(accessToken);
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
            CheckToken(accessToken);
            CheckDenied(folderId);
            string id = $"file-{++_nextId}";
            FakeFile file = new(id, name, "text/markdown", [folderId], content, Stamp(), false, $"https://drive.test/file/{id}");
            Files[id] = file;
            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new DriveUpstreamException("simulated create timeout after apply");
            }

            return Task.FromResult(file.ToDriveFile());
        }

        public async Task<DriveFile> UpdateContentAsync(
            string accessToken,
            string fileId,
            string content,
            string? newName,
            CancellationToken cancellationToken = default)
        {
            CheckToken(accessToken);
            CheckDenied(fileId);
            if (FailUpdatesFor.Contains(fileId))
            {
                throw new DriveUpstreamException("simulated update failure");
            }

            if (BlockUpdates is not null)
            {
                EnteredUpdate.TrySetResult(null);
                await BlockUpdates.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                CheckToken(accessToken);
                CheckDenied(fileId);
            }

            FakeFile file = Files[fileId];
            FakeFile updated = file with { Content = content, Name = newName ?? file.Name, ModifiedTime = Stamp() };
            Files[fileId] = updated;
            return updated.ToDriveFile();
        }

        private void CheckToken(string accessToken)
        {
            if (OnlyAcceptTokens.Count > 0 && !OnlyAcceptTokens.Contains(accessToken))
            {
                throw new DriveApiException(HttpStatusCode.Unauthorized, "stale token");
            }
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
