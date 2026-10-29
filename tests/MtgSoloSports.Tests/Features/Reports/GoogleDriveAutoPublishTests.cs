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

/// <summary>
/// MSS-090: opt-in automatic Google Drive publishing after each completed
/// global stage. The worker reuses the MSS-089 publisher; these tests prove
/// the trigger boundaries, coalescing, isolation and recovery behaviour
/// without touching scoring, RNG or save sporting state.
/// </summary>
public sealed class GoogleDriveAutoPublishTests
{
    [Fact]
    public void AutoPublishStatus_DerivationNeverImpliesSuccess()
    {
        Guid saveId = Guid.NewGuid();
        DrivePublishingStore.DestinationDocument Disabled() => Doc(saveId, enabled: false);
        DriveAutoPublishStatus.Compute(null, connected: true, publishing: false).ShouldBe("Disabled");
        DriveAutoPublishStatus.Compute(Disabled(), connected: false, publishing: false).ShouldBe("Disabled");

        // Enabled but never requested: nothing pending, up to date.
        var fresh = Doc(saveId, enabled: true);
        DriveAutoPublishStatus.HasPendingRequest(fresh).ShouldBeFalse();
        DriveAutoPublishStatus.IsCaughtUp(fresh).ShouldBeTrue();
        DriveAutoPublishStatus.Compute(fresh, connected: true, publishing: false).ShouldBe("UpToDate");

        // Requested without a publish: pending, and disconnected without an account.
        var pending = fresh with
        {
            AutoRequestedSeason = 2,
            AutoRequestedGlobalStage = 5,
            AutoRequestedPhase = "SeasonInProgress",
            AutoRequestedUtc = DateTimeOffset.UtcNow,
        };
        DriveAutoPublishStatus.HasPendingRequest(pending).ShouldBeTrue();
        DriveAutoPublishStatus.IsCaughtUp(pending).ShouldBeFalse();
        DriveAutoPublishStatus.Compute(pending, connected: false, publishing: false).ShouldBe("Disconnected");
        DriveAutoPublishStatus.Compute(pending, connected: true, publishing: true).ShouldBe("Publishing");
        DriveAutoPublishStatus.Compute(pending, connected: true, publishing: false).ShouldBe("Pending");

        // A recorded failure surfaces as Failed, never as success.
        var failed = pending with { AutoLastError = "Google Drive is temporarily unavailable (503)." };
        DriveAutoPublishStatus.Compute(failed, connected: true, publishing: false).ShouldBe("Failed");

        // An auth pause surfaces as Disconnected so the user reconnects.
        var paused = pending with { AutoPausedForAuth = true, AutoLastError = "expired" };
        DriveAutoPublishStatus.Compute(paused, connected: true, publishing: false).ShouldBe("Disconnected");

        // Matching checkpoints are up to date even with a stale error left
        // behind by an earlier manual catch-up.
        var caughtUp = pending with
        {
            LastSeasonNumber = 2,
            LastGlobalStage = 5,
            LastPhase = "SeasonInProgress",
            LastPublishedUtc = DateTimeOffset.UtcNow,
            AutoLastError = "stale",
        };
        DriveAutoPublishStatus.IsCaughtUp(caughtUp).ShouldBeTrue();
        DriveAutoPublishStatus.HasPendingRequest(caughtUp).ShouldBeFalse();
        DriveAutoPublishStatus.Compute(caughtUp, connected: true, publishing: false).ShouldBe("UpToDate");
    }

    [Fact]
    public async Task AutoPublish_DefaultsOffAndToggleRoundTrips()
    {
        var (factory, root, _, _) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            AutoState initial = await GetAutoStateAsync(client, saveId);
            initial.Enabled.ShouldBeFalse();
            initial.Status.ShouldBe("Disabled");

            using HttpResponseMessage toggle = await client.PutAsync(
                $"/api/saves/{saveId:D}/reports/drive/auto-publish",
                new StringContent("""{"enabled":true}""", Encoding.UTF8, "application/json"));
            toggle.StatusCode.ShouldBe(HttpStatusCode.OK);
            AutoState enabled = await GetAutoStateAsync(client, saveId);
            enabled.Enabled.ShouldBeTrue();
            enabled.RequestedSeason.ShouldNotBeNull();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisabledMode_CompletingGlobalStageUploadsNothing()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult baseline = await PostPublishAsync(client, saveId, allowCreateMissing: true);
            baseline.Status.ShouldBe(HttpStatusCode.OK);
            string dossierId = baseline.FileId("dossier")!;
            string contentBefore = drive.Files[dossierId].Content;

            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            await Task.Delay(TimeSpan.FromSeconds(3));

            AutoState state = await GetAutoStateAsync(client, saveId);
            state.Enabled.ShouldBeFalse();
            state.Status.ShouldBe("Disabled");
            drive.Files[dossierId].Content.ShouldBe(contentBefore);
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? destination =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            destination.ShouldNotBeNull();
            destination!.AutoRequestedSeason.ShouldBeNull();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedRound_DoesNotTriggerAutoPublish()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            _ = await PostPublishAsync(client, saveId, allowCreateMissing: true);
            await PutAutoPublishAsync(client, saveId, enabled: true);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);
            AutoState settled = await GetAutoStateAsync(client, saveId);
            settled.Status.ShouldBe("UpToDate");
            string? requestedBefore = settled.RequestedUtc;

            // Advance a single round in the first league of the current season.
            int leagueId = await FirstLeagueIdAsync(client, saveId);
            using HttpResponseMessage round = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/rounds/advance", null);
            round.StatusCode.ShouldBe(HttpStatusCode.OK);
            await Task.Delay(TimeSpan.FromSeconds(3));

            AutoState after = await GetAutoStateAsync(client, saveId);
            after.RequestedUtc.ShouldBe(requestedBefore);
            after.Status.ShouldBe("UpToDate");
            _ = drive;
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PartialLeagueStage_DoesNotTriggerAutoPublish()
    {
        var (factory, root, _, _) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            _ = await PostPublishAsync(client, saveId, allowCreateMissing: true);
            await PutAutoPublishAsync(client, saveId, enabled: true);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);
            AutoState settled = await GetAutoStateAsync(client, saveId);
            int stageBefore = settled.RequestedStage!.Value;

            int leagueId = await FirstLeagueIdAsync(client, saveId);
            using HttpResponseMessage completed = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/stages/complete", null);
            completed.StatusCode.ShouldBe(HttpStatusCode.OK);
            await Task.Delay(TimeSpan.FromSeconds(3));

            // One league of many: the global stage has not advanced, so the
            // requested checkpoint must be unchanged.
            AutoState after = await GetAutoStateAsync(client, saveId);
            after.RequestedStage.ShouldBe(stageBefore);
            after.Status.ShouldBe("UpToDate");
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedGlobalStage_AutoPublishesSameFilesInPlace()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult baseline = await PostPublishAsync(client, saveId, allowCreateMissing: true);
            string dossierId = baseline.FileId("dossier")!;
            string almanacId = baseline.FileId("almanac")!;
            int filesBefore = drive.Files.Count;
            await PutAutoPublishAsync(client, saveId, enabled: true);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);
            int stageBefore = (await GetAutoStateAsync(client, saveId)).RequestedStage!.Value;

            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            await WaitForAutoStageAsync(client, saveId, stageBefore + 1, timeoutSeconds: 30);

            AutoState after = await GetAutoStateAsync(client, saveId);
            after.Status.ShouldBe("UpToDate");
            after.LastStage.ShouldBe(stageBefore + 1);
            drive.Files.Count.ShouldBe(filesBefore);
            drive.Files[dossierId].Content.ShouldContain("# Season dossier");
            drive.Files[almanacId].Content.ShouldContain("# Universe almanac");
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
    public async Task BurstStages_CoalesceWithoutDuplicates()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult baseline = await PostPublishAsync(client, saveId, allowCreateMissing: true);
            string dossierId = baseline.FileId("dossier")!;
            int filesBefore = drive.Files.Count;
            await PutAutoPublishAsync(client, saveId, enabled: true);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);
            int stageBefore = (await GetAutoStateAsync(client, saveId)).RequestedStage!.Value;

            // Burst of three global stages back to back: the pump must settle
            // on the newest durable state with the same Drive files.
            for (int i = 0; i < 3; i++)
            {
                using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
                bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            await WaitForAutoStageAsync(client, saveId, stageBefore + 3, timeoutSeconds: 60);
            AutoState after = await GetAutoStateAsync(client, saveId);
            after.Status.ShouldBe("UpToDate");
            drive.Files.Count.ShouldBe(filesBefore);
            drive.Files.ShouldContainKey(dossierId);
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
    public async Task ManualPublishDuringAuto_DoesNotDuplicateOrLose()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            _ = await PostPublishAsync(client, saveId, allowCreateMissing: true);
            await PutAutoPublishAsync(client, saveId, enabled: true);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);
            int filesBefore = drive.Files.Count;

            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            // Race a manual override against the automatic attempt: the
            // shared per-save gate serializes them, so no twin files appear
            // and the newest state wins.
            Task<PublishResult> manual = PostPublishAsync(client, saveId);
            Task<PublishResult> retry = PostRetryAndWaitAsync(client, saveId);
            PublishResult[] done = await Task.WhenAll(manual, retry);
            done[0].Status.ShouldBe(HttpStatusCode.OK);
            drive.Files.Count.ShouldBe(filesBefore);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisablingAuto_CancelsPendingWithoutDeletingFiles()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult baseline = await PostPublishAsync(client, saveId, allowCreateMissing: true);
            string dossierId = baseline.FileId("dossier")!;
            string contentBefore = drive.Files[dossierId].Content;
            int filesBefore = drive.Files.Count;
            await PutAutoPublishAsync(client, saveId, enabled: true);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);
            await PutAutoPublishAsync(client, saveId, enabled: false);

            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            await Task.Delay(TimeSpan.FromSeconds(3));

            AutoState after = await GetAutoStateAsync(client, saveId);
            after.Enabled.ShouldBeFalse();
            after.Status.ShouldBe("Disabled");
            after.RequestedSeason.ShouldBeNull();
            drive.Files.Count.ShouldBe(filesBefore);
            drive.Files[dossierId].Content.ShouldBe(contentBefore);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExpiredCredentials_PauseAndReconnectResumes()
    {
        var (factory, root, oauth, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            _ = await PostPublishAsync(client, saveId, allowCreateMissing: true);
            await PutAutoPublishAsync(client, saveId, enabled: true);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);
            _ = oauth;
            _ = drive;

            // Expire the grant: the next automatic attempt must pause with a
            // disconnected status instead of implying success.
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            await store.SaveConnectionAsync(new DrivePublishingStore.ConnectionDocument(
                "bad-refresh", null, DriveScopes.DriveFile, DateTimeOffset.UtcNow,
                "stale-access", DateTimeOffset.UtcNow.AddSeconds(-60)), CancellationToken.None);
            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            AutoState paused = await WaitForAutoStatusAsync(
                client, saveId, ["Disconnected", "Failed", "Pending"], timeoutSeconds: 30);
            paused.LastError.ShouldNotBeNullOrWhiteSpace();
            string.Equals(paused.Status, "UpToDate", StringComparison.Ordinal).ShouldBeFalse();

            // Sporting progression itself was unaffected by the offline auth.
            using HttpResponseMessage bulkAgain = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulkAgain.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Reconnect through the OAuth callback: pending work catches up
            // automatically without implying success before the upload lands.
            await ConnectOverHttpAsync(factory, client, saveId);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 60);
            AutoState resumed = await GetAutoStateAsync(client, saveId);
            resumed.LastSeason.ShouldNotBeNull();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestartWithPendingCheckpoint_RecoversOnStartup()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-autopub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FakeOAuthClient oauth = new();
        FakeDriveClient drive = new();
        WebApplicationFactory<Program> factory = CreateFactoryWithRoot(root, oauth, drive);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            _ = await PostPublishAsync(client, saveId, allowCreateMissing: true);
            await PutAutoPublishAsync(client, saveId, enabled: true);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);

            // Leave an unpublished committed checkpoint behind, then stop the
            // process before the pump can upload it.
            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            AutoState pending = await WaitForPendingAsync(client, saveId, timeoutSeconds: 30);
            pending.RequestedSeason.ShouldNotBeNull();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
        }

        // A fresh process over the same on-disk roots must reconcile the
        // persisted requested checkpoint and publish it.
        WebApplicationFactory<Program> restarted = CreateFactoryWithRoot(root, oauth, drive);
        try
        {
            using HttpClient client = restarted.CreateClient();
            Guid saveId = await ReadOnlySaveIdAsync(root);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 60);
            AutoState recovered = await GetAutoStateAsync(client, saveId);
            recovered.Status.ShouldBe("UpToDate");
            recovered.LastSeason.ShouldBe(recovered.RequestedSeason);
        }
        finally
        {
            restarted.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NewSeason_CreatesNewDossierPreservingHistory()
    {
        var (factory, root, _, drive) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();
            await SeedConnectionAsync(factory);
            await PutDestinationAsync(client, saveId, """{"mtshistoryFolderId":"folder-mtshistory"}""");
            PublishResult first = await PostPublishAsync(client, saveId, allowCreateMissing: true);
            first.Status.ShouldBe(HttpStatusCode.OK);
            string dossierIdSeason1 = first.FileId("dossier")!;
            string almanacId = first.FileId("almanac")!;
            string season1Content = drive.Files[dossierIdSeason1].Content;
            int filesBefore = drive.Files.Count;
            await PutAutoPublishAsync(client, saveId, enabled: true);
            await WaitForAutoUpToDateAsync(client, saveId, timeoutSeconds: 30);

            // Postseason plus next-season start from a complete Season 1:
            // only lifecycle steps, no bulk stage simulation needed.
            using HttpResponseMessage simulated = await client.PostAsync(
                $"/api/saves/{saveId:D}/simulate-seasons",
                new StringContent("""{"seasons":1}""", Encoding.UTF8, "application/json"));
            simulated.StatusCode.ShouldBe(HttpStatusCode.OK);
            await WaitForNewSeasonAsync(client, saveId, newSeason: 2, timeoutSeconds: 120);

            drive.Files.Count.ShouldBe(filesBefore + 1);
            drive.Files.ShouldContainKey(dossierIdSeason1);
            drive.Files[dossierIdSeason1].Content.ShouldBe(season1Content);
            drive.Files.ShouldContainKey(almanacId);
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? after =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            after.ShouldNotBeNull();
            after!.DossierBySeason.ShouldNotBeNull();
            after.DossierBySeason!.ShouldContainKey(1);
            after.DossierBySeason.ShouldContainKey(2);
            after.DossierBySeason[1].FileId.ShouldBe(dossierIdSeason1);
            string.Equals(after.DossierBySeason[2].FileId, dossierIdSeason1, StringComparison.Ordinal).ShouldBeFalse();
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static DrivePublishingStore.DestinationDocument Doc(Guid saveId, bool enabled) =>
        new(saveId, "Save", "folder-mtshistory", "mtshistory", "folder-seasons",
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            AutoPublishEnabled: enabled);

    private sealed record AutoState(
        bool Enabled,
        string Status,
        int? RequestedSeason,
        int? RequestedStage,
        string? RequestedPhase,
        string? RequestedUtc,
        int? LastSeason,
        int? LastStage,
        string? LastError);

    private static async Task<AutoState> GetAutoStateAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/reports/drive/status").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        JsonElement root = json.RootElement;
        return new AutoState(
            root.GetProperty("autoPublishEnabled").GetBoolean(),
            root.GetProperty("autoPublishStatus").GetString()!,
            GetNullableInt(root, "autoRequestedSeason"),
            GetNullableInt(root, "autoRequestedGlobalStage"),
            GetNullableString(root, "autoRequestedPhase"),
            GetNullableString(root, "autoRequestedUtc"),
            GetNullableInt(root, "lastSeasonNumber"),
            GetNullableInt(root, "lastGlobalStage"),
            GetNullableString(root, "autoLastError"));
    }

    private static int? GetNullableInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static string? GetNullableString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task PutAutoPublishAsync(HttpClient client, Guid saveId, bool enabled)
    {
        using HttpResponseMessage response = await client.PutAsync(
            $"/api/saves/{saveId:D}/reports/drive/auto-publish",
            new StringContent($"{{\"enabled\":{enabled.ToString().ToLowerInvariant()}}}", Encoding.UTF8, "application/json")).ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<PublishResult> PostRetryAndWaitAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/auto-publish/retry", null).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new PublishResult(response.StatusCode, [], body);
        }

        return new PublishResult(response.StatusCode, [], body);
    }

    private static async Task WaitForAutoUpToDateAsync(HttpClient client, Guid saveId, int timeoutSeconds)
    {
        AutoState state = await WaitForAutoStatusAsync(client, saveId, ["UpToDate"], timeoutSeconds).ConfigureAwait(false);
        state.Status.ShouldBe("UpToDate");
    }

    private static async Task<AutoState> WaitForAutoStatusAsync(
        HttpClient client,
        Guid saveId,
        string[] wanted,
        int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        AutoState latest = await GetAutoStateAsync(client, saveId).ConfigureAwait(false);
        while (!wanted.Contains(latest.Status, StringComparer.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Timed out waiting for auto-publish status {string.Join("/", wanted)}; last was {latest.Status} (requested {latest.RequestedSeason}/{latest.RequestedStage}, last {latest.LastSeason}/{latest.LastStage}, error: {latest.LastError}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            latest = await GetAutoStateAsync(client, saveId).ConfigureAwait(false);
        }

        return latest;
    }

    private static async Task<AutoState> WaitForPendingAsync(HttpClient client, Guid saveId, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        AutoState latest = await GetAutoStateAsync(client, saveId).ConfigureAwait(false);
        while (!string.Equals(latest.Status, "UpToDate", StringComparison.Ordinal) || latest.RequestedSeason is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Timed out waiting for a pending auto-publish request; last status was {latest.Status}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            latest = await GetAutoStateAsync(client, saveId).ConfigureAwait(false);
        }

        return latest;
    }

    private static async Task WaitForAutoStageAsync(HttpClient client, Guid saveId, int stage, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            AutoState latest = await GetAutoStateAsync(client, saveId).ConfigureAwait(false);
            if (string.Equals(latest.Status, "UpToDate", StringComparison.Ordinal)
                && latest.LastStage == stage)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Timed out waiting for auto-published stage {stage}; last was {latest.Status} at {latest.LastSeason}/{latest.LastStage} (requested {latest.RequestedSeason}/{latest.RequestedStage}, error: {latest.LastError}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
    }

    private static async Task WaitForNewSeasonAsync(HttpClient client, Guid saveId, int newSeason, int timeoutSeconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            AutoState latest = await GetAutoStateAsync(client, saveId).ConfigureAwait(false);
            if (string.Equals(latest.Status, "UpToDate", StringComparison.Ordinal)
                && latest.LastSeason == newSeason)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Timed out waiting for auto-published season {newSeason}; last was {latest.Status} at {latest.LastSeason}/{latest.LastStage} (error: {latest.LastError}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
    }

    private static async Task<int> FirstLeagueIdAsync(HttpClient client, Guid saveId)
    {
        int season = await ReadCurrentSeasonAsync(client, saveId).ConfigureAwait(false);
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/saves/{saveId:D}/seasons/{season}/progress").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        JsonElement leagues = json.RootElement.GetProperty("leagues");
        leagues.GetArrayLength().ShouldBeGreaterThan(1);
        return leagues[0].GetProperty("leagueId").GetInt32();
    }

    private static async Task<Guid> ReadOnlySaveIdAsync(string root)
    {
        string[] files = Directory.GetFiles(Path.Combine(root, "saves"), "*.db");
        files.Length.ShouldBe(1);
        string name = Path.GetFileNameWithoutExtension(files[0]);
        return Guid.ParseExact(name, "N");
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
        bool confirmCrossSave = false,
        bool allowOverwrite = false,
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

    private static (WebApplicationFactory<Program> Factory, string Root, FakeOAuthClient OAuth, FakeDriveClient Drive) CreateFactory(
        bool configured = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-autopub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FakeOAuthClient oauth = new();
        FakeDriveClient drive = new();
        return (CreateFactoryWithRoot(root, oauth, drive, configured), root, oauth, drive);
    }

    private static WebApplicationFactory<Program> CreateFactoryWithRoot(
        string root,
        FakeOAuthClient oauth,
        FakeDriveClient drive,
        bool configured = true)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(refreshToken, "bad-refresh", StringComparison.Ordinal))
            {
                throw new DriveNotConnectedException("expired");
            }

            return Task.FromResult(new OAuthTokenResponse("access-refreshed", 3600, refreshToken, DriveScopes.DriveFile));
        }

        public Task<bool> TryRevokeAsync(string token, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeDriveClient : IGoogleDriveClient
    {
        public readonly Dictionary<string, FakeFile> Files = new(StringComparer.Ordinal);

        public readonly HashSet<string> FailUpdatesFor = new(StringComparer.Ordinal);

        public readonly HashSet<string> OnlyAcceptTokens = new(StringComparer.Ordinal);

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
            return Task.FromResult(file.ToDriveFile());
        }

        public Task<DriveFile> UpdateContentAsync(
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

            FakeFile file = Files[fileId];
            FakeFile updated = file with { Content = content, Name = newName ?? file.Name, ModifiedTime = Stamp() };
            Files[fileId] = updated;
            return Task.FromResult(updated.ToDriveFile());
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
