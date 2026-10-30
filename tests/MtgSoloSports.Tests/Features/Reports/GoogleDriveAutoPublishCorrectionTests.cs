using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

/// <summary>
/// MSS-090 correction: the trusted review found two P1 defects — the pump
/// abandoned ChannelReader.ReadAsync waiters on every poll timeout (losing
/// wakeups), and post-commit checkpoint notes honored the HTTP request token
/// (a cancelled client could lose a committed stage). These deterministic tests
/// pin both fixes without touching scoring, RNG or save sporting state.
/// </summary>
public sealed class GoogleDriveAutoPublishCorrectionTests
{
    [Fact]
    public async Task Notify_WithCancelledRequestToken_StillPersistsCheckpoint()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-autopub-cancel-");
        string driveRoot = Path.Combine(Path.GetTempPath(), "mtgsolosports-autopub-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(driveRoot);
        try
        {
            DrivePublishingStore publishing = CreatePublishingStore(driveRoot);
            GetSeasonStatusHandler status = new(store);
            DriveAutoPublishTracker tracker = new();

            GetSeasonStatusResponse current = await status.HandleAsync(saveId, CancellationToken.None);
            DrivePublishingStore.DestinationDocument seed = new(
                saveId,
                "Save",
                "folder-mtshistory",
                "mtshistory",
                "folder-seasons",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                AutoPublishEnabled: true);
            await publishing.SaveDestinationAsync(seed, CancellationToken.None);

            DriveAutoPublishNotifier notifier = new(
                store,
                publishing,
                tracker,
                status,
                TimeProvider.System,
                NullLogger<DriveAutoPublishNotifier>.Instance);

            using CancellationTokenSource cts = new();
            await cts.CancelAsync();

            // Must not throw, even with an already-cancelled request token.
            await notifier.NotifyStageCheckpointAsync(saveId, cts.Token);

            DrivePublishingStore.DestinationDocument? after =
                await publishing.GetDestinationAsync(saveId, CancellationToken.None);
            after.ShouldNotBeNull();
            after!.AutoRequestedSeason.ShouldBe(current.CurrentSeasonNumber);
            after.AutoRequestedGlobalStage.ShouldBe(current.GlobalStage);
            after.AutoRequestedPhase.ShouldBe(current.ComputedPhase);
            after.AutoRequestedUtc.ShouldNotBeNull();

            // A pending request must have signalled the pump.
            tracker.Reader.TryRead(out Guid signaled).ShouldBeTrue();
            signaled.ShouldBe(saveId);
            DriveAutoPublishStatus.HasPendingRequest(after).ShouldBeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(driveRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Pump_WaitSurvivesIdlePolls_WithoutLosingSignal()
    {
        TimeSpan previous = DriveAutoPublishWorker.SignalPollInterval;
        DriveAutoPublishWorker.SignalPollInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            DriveAutoPublishTracker tracker = new();
            DriveAutoPublishWorker worker = new(
                new ThrowingScopeFactory(),
                tracker,
                TimeProvider.System,
                NullLogger<DriveAutoPublishWorker>.Instance);

            using CancellationTokenSource shutdown = new(TimeSpan.FromSeconds(30));

            // Simulate an idle worker: several consecutive poll timeouts must
            // not leave orphaned channel readers behind.
            for (int i = 0; i < 5; i++)
            {
                Guid? idle = await worker.WaitForSignalAsync(shutdown.Token);
                idle.ShouldBeNull();
            }

            Guid saveId = Guid.NewGuid();
            tracker.Signal(saveId);

            Guid? received = await worker.WaitForSignalAsync(shutdown.Token);
            received.ShouldBe(saveId);
        }
        finally
        {
            DriveAutoPublishWorker.SignalPollInterval = previous;
        }
    }

    [Fact]
    public async Task PartialPublish_DoesNotAdvanceSharedCheckpoint()
    {
        // P1: dossier fails (stale remote edit) while almanac settles. The
        // shared Last* checkpoint must stay at the prior stage so the pending
        // request remains visible and status reports Failed, never UpToDate.
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
            DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
            DrivePublishingStore.DestinationDocument? before =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            before.ShouldNotBeNull();
            before!.LastSeasonNumber.ShouldNotBeNull();
            int lastSeasonBefore = before.LastSeasonNumber!.Value;
            int? lastStageBefore = before.LastGlobalStage;

            // Advance sporting state so the next publish has new content for
            // both reports, then hand-edit the dossier remote to force a
            // stale-edit failure without overwrite confirmation.
            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            drive.Files[dossierId] = drive.Files[dossierId] with { Content = "# hand-edited by user\n" };

            PublishResult partial = await PostPublishAsync(client, saveId);
            partial.Status.ShouldBe(HttpStatusCode.OK);
            partial.Outcome("dossier").ShouldBe("failed");
            partial.Detail("dossier")!.ShouldContain("edited independently");
            string.Equals(partial.Outcome("almanac"), "failed", StringComparison.Ordinal).ShouldBeFalse();

            DrivePublishingStore.DestinationDocument? after =
                await store.GetDestinationAsync(saveId, CancellationToken.None);
            AssertSharedCheckpointUnchanged(after, lastSeasonBefore, lastStageBefore);

            // With an auto request outstanding for the new stage, the stale
            // checkpoint stays pending and surfaces as Failed, never UpToDate.
            MtgSoloSports.Persistence.Saves.SaveStore saveStore =
                factory.Services.GetRequiredService<MtgSoloSports.Persistence.Saves.SaveStore>();
            GetSeasonStatusHandler statusHandler = new(saveStore);
            GetSeasonStatusResponse live =
                await statusHandler.HandleAsync(saveId, CancellationToken.None);
            AssertPendingFailed(after!, live);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CompletedGlobalStage_IgnoresRequestCancellation()
    {
        // P1: the post-commit boundary read must survive HTTP disconnect. A
        // cancelled request token must not suppress the checkpoint note.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-autopub-boundary-");
        try
        {
            CompleteStageHandler handler = new(store);
            MethodInfo? method = typeof(CompleteStageHandler).GetMethod(
                "CompletedGlobalStageAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            method.ShouldNotBeNull();

            // Stage 0 never equals a real global stage (>= 1), so a live read
            // must report a global advance. With the bug, a cancelled token
            // throws inside the DB reads and the catch returns false.
            CompleteStageResponse dummy = new(
                saveId, 1, 1, "League", 0, 1, 16, "checksum",
                0UL, 0UL, 0UL, 0UL, 2, []);
            using CancellationTokenSource cts = new();
            await cts.CancelAsync();

            Task<bool>? pending = method!.Invoke(handler, [saveId, dummy, cts.Token]) as Task<bool>;
            Assert.True(pending is not null);
            (await pending!).ShouldBeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task BulkHandlers_WireNotifier_SoCancelledBulkKeepsCheckpoint()
    {
        // P1 (MSS-090 correction): bulk/fast paths commit global stages
        // incrementally but previously notified only after the whole request.
        // The inner bulk operation must carry the notifier so an intermediate
        // commit is durably requested even when the outer request cancels.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-autopub-bulkwire-");
        string driveRoot = Path.Combine(Path.GetTempPath(), "mtgsolosports-autopub-bulkwire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(driveRoot);
        try
        {
            DrivePublishingStore publishing = CreatePublishingStore(driveRoot);
            GetSeasonStatusHandler status = new(store);
            DriveAutoPublishTracker tracker = new();
            DriveAutoPublishNotifier notifier = new(
                store,
                publishing,
                tracker,
                status,
                TimeProvider.System,
                NullLogger<DriveAutoPublishNotifier>.Instance);

            // SimulateSeasons must propagate the notifier to its inner bulk
            // stage handler (previously constructed without a notifier).
            MtgSoloSports.Features.Simulation.SimulateSeasons.SimulateSeasonsHandler bulkSeasons =
                new(store, notifier);
            var handlers = bulkSeasons.CreateHandlers();
            FieldInfo? bulkField = typeof(MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues.CompleteStageForAllLeaguesHandler)
                .GetField("_autoPublish", BindingFlags.NonPublic | BindingFlags.Instance);
            bulkField.ShouldNotBeNull();
            bulkField!.GetValue(handlers.BulkStages).ShouldBeSameAs(notifier);

            // CompleteSeason must also carry the notifier into its remaining
            // stages loop (previously post-success-only).
            MtgSoloSports.Features.Simulation.CompleteSeason.CompleteSeasonHandler bulkSeason =
                new(store, notifier);
            _ = saveId;
            _ = bulkSeason;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(driveRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CompleteSeason_FailureStillNotesCheckpoint_ViaFinally()
    {
        // P1: CompleteSeason notified only after success. A cancelled/failed
        // bulk request after an intermediate commit left no AutoRequested
        // checkpoint. The outer note must run even when the sporting work
        // throws, using an unlinked token and never masking the error.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-autopub-seasonfinally-");
        string driveRoot = Path.Combine(Path.GetTempPath(), "mtgsolosports-autopub-seasonfinally-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(driveRoot);
        try
        {
            DrivePublishingStore publishing = CreatePublishingStore(driveRoot);
            GetSeasonStatusHandler status = new(store);
            DriveAutoPublishTracker tracker = new();
            DriveAutoPublishNotifier notifier = new(
                store,
                publishing,
                tracker,
                status,
                TimeProvider.System,
                NullLogger<DriveAutoPublishNotifier>.Instance);

            GetSeasonStatusResponse current = await status.HandleAsync(saveId, CancellationToken.None);
            DrivePublishingStore.DestinationDocument seed = new(
                saveId,
                "Save",
                "folder-mtshistory",
                "mtshistory",
                "folder-seasons",
                null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                AutoPublishEnabled: true);
            await publishing.SaveDestinationAsync(seed, CancellationToken.None);

            // Season 1 is already complete, so this throws without new
            // commits; the finally note must still record the durable state.
            MtgSoloSports.Features.Simulation.CompleteSeason.CompleteSeasonHandler handler =
                new(store, notifier);
            await Should.ThrowAsync<MtgSoloSports.Features.Simulation.CompleteSeason.CompleteSeasonConflictException>(
                () => handler.HandleAsync(saveId, CancellationToken.None));

            DrivePublishingStore.DestinationDocument? after =
                await publishing.GetDestinationAsync(saveId, CancellationToken.None);
            after.ShouldNotBeNull();
            after!.AutoRequestedSeason.ShouldBe(current.CurrentSeasonNumber);
            after.AutoRequestedGlobalStage.ShouldBe(current.GlobalStage);
            after.AutoRequestedPhase.ShouldBe(current.ComputedPhase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(driveRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RunningWorker_ReconcilesMissingCheckpoint_WithoutRestart()
    {
        // P1: the worker reconciled missing checkpoints only at startup.
        // A bulk request cancelled after an intermediate commit left the
        // durable save ahead with no AutoRequested note, and the running
        // worker never healed it (status falsely UpToDate). ProcessSaveAsync
        // must now reconcile the durable checkpoint before deciding there is
        // nothing to do, even while disconnected (no Drive upload attempted).
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-autopub-running-");
        string driveRoot = Path.Combine(Path.GetTempPath(), "mtgsolosports-autopub-running-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(driveRoot);
        try
        {
            DrivePublishingStore publishing = CreatePublishingStore(driveRoot);
            GetSeasonStatusHandler status = new(store);
            DriveAutoPublishTracker tracker = new();
            DriveAutoPublishNotifier notifier = new(
                store,
                publishing,
                tracker,
                status,
                TimeProvider.System,
                NullLogger<DriveAutoPublishNotifier>.Instance);

            GetSeasonStatusResponse before = await SeedUpToDateAsync(publishing, status, saveId);
            await AdvanceOneStageWithoutNotifyAsync(store, saveId, before.GlobalStage);
            await AssertStaleUpToDateAsync(publishing, saveId);
            await HealViaRunningWorkerAsync(publishing, notifier, tracker, saveId);
            await AssertHealedAsync(publishing, status, saveId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(driveRoot, recursive: true); } catch { }
        }
    }

    private static async Task<GetSeasonStatusResponse> SeedUpToDateAsync(
        DrivePublishingStore publishing,
        GetSeasonStatusHandler status,
        Guid saveId)
    {
        GetSeasonStatusResponse before = await status.HandleAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        DrivePublishingStore.DestinationDocument blank = new(
            saveId,
            "Save",
            "folder-mtshistory",
            "mtshistory",
            "folder-seasons",
            null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            AutoPublishEnabled: true);
        DrivePublishingStore.DestinationDocument seed = blank with
        {
            AutoRequestedSeason = before.CurrentSeasonNumber,
            AutoRequestedGlobalStage = before.GlobalStage,
            AutoRequestedPhase = before.ComputedPhase,
            AutoRequestedUtc = DateTimeOffset.UtcNow,
            LastSeasonNumber = before.CurrentSeasonNumber,
            LastGlobalStage = before.GlobalStage,
            LastPhase = before.ComputedPhase,
            LastPublishedUtc = DateTimeOffset.UtcNow,
        };
        await publishing.SaveDestinationAsync(seed, CancellationToken.None).ConfigureAwait(false);
        return before;
    }

    private static async Task AdvanceOneStageWithoutNotifyAsync(
        MtgSoloSports.Persistence.Saves.SaveStore store,
        Guid saveId,
        int stageBefore)
    {
        var withoutNotify = new MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues.CompleteStageForAllLeaguesHandler(store);
        var completed = await withoutNotify.HandleAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        completed.GlobalStageAfter.ShouldBeGreaterThan(stageBefore);
    }

    private static async Task AssertStaleUpToDateAsync(
        DrivePublishingStore publishing,
        Guid saveId)
    {
        DrivePublishingStore.DestinationDocument? stale =
            await publishing.GetDestinationAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        stale.ShouldNotBeNull();
        DriveAutoPublishStatus.HasPendingRequest(stale!).ShouldBeFalse();
        // When connected, the missing note falsely reports UpToDate instead
        // of Pending; the worker must heal it to a pending request.
        DriveAutoPublishStatus.Compute(stale, connected: true, publishing: false).ShouldBe("UpToDate");
    }

    private static async Task HealViaRunningWorkerAsync(
        DrivePublishingStore publishing,
        DriveAutoPublishNotifier notifier,
        DriveAutoPublishTracker tracker,
        Guid saveId)
    {
        var scopes = new TestScopeFactory(publishing, notifier);
        DriveAutoPublishWorker worker = new(
            scopes,
            tracker,
            TimeProvider.System,
            NullLogger<DriveAutoPublishWorker>.Instance);
        await worker.ProcessSaveAsync(saveId, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task AssertHealedAsync(
        DrivePublishingStore publishing,
        GetSeasonStatusHandler status,
        Guid saveId)
    {
        DrivePublishingStore.DestinationDocument? healed =
            await publishing.GetDestinationAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        healed.ShouldNotBeNull();
        GetSeasonStatusResponse live = await status.HandleAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        healed!.AutoRequestedSeason.ShouldBe(live.CurrentSeasonNumber);
        healed.AutoRequestedGlobalStage.ShouldBe(live.GlobalStage);
        healed.AutoRequestedPhase.ShouldBe(live.ComputedPhase);
        DriveAutoPublishStatus.HasPendingRequest(healed).ShouldBeTrue();
    }

    private sealed class TestScopeFactory(
        DrivePublishingStore publishing,
        DriveAutoPublishNotifier? notifier) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new TestScope(publishing, notifier);

        private sealed class TestScope(
            DrivePublishingStore publishing,
            DriveAutoPublishNotifier? notifier) : IServiceScope
        {
            private readonly TestProvider _provider = new(publishing, notifier);
            public IServiceProvider ServiceProvider => _provider;
            public void Dispose()
            {
            }
        }

        private sealed class TestProvider(
            DrivePublishingStore publishing,
            DriveAutoPublishNotifier? notifier) : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(DrivePublishingStore))
                {
                    return publishing;
                }

                if (serviceType == typeof(DriveAutoPublishNotifier))
                {
                    return notifier;
                }

                return null;
            }
        }
    }

    private static void AssertSharedCheckpointUnchanged(
        DrivePublishingStore.DestinationDocument? after,
        int lastSeasonBefore,
        int? lastStageBefore)
    {
        after.ShouldNotBeNull();
        // Shared checkpoint must not advance on a partial success.
        after!.LastSeasonNumber.ShouldBe(lastSeasonBefore);
        after.LastGlobalStage.ShouldBe(lastStageBefore);
        after.LastError.ShouldNotBeNullOrWhiteSpace();
        after.LastError.ShouldContain("dossier");
        after.LastDossierOutcome.ShouldBe("failed");
        string.Equals(after.LastAlmanacOutcome, "failed", StringComparison.Ordinal).ShouldBeFalse();
    }

    private static void AssertPendingFailed(
        DrivePublishingStore.DestinationDocument after,
        GetSeasonStatusResponse live)
    {
        var withRequest = after with
        {
            AutoPublishEnabled = true,
            AutoRequestedSeason = live.CurrentSeasonNumber,
            AutoRequestedGlobalStage = live.GlobalStage,
            AutoRequestedPhase = live.ComputedPhase,
            AutoRequestedUtc = DateTimeOffset.UtcNow,
            AutoLastError = after.LastError,
        };
        DriveAutoPublishStatus.HasPendingRequest(withRequest).ShouldBeTrue();
        DriveAutoPublishStatus.Compute(withRequest, connected: true, publishing: false).ShouldBe("Failed");
    }

    private static DrivePublishingStore CreatePublishingStore(string root)
    {
        IOptions<DrivePublishingOptions> options = Options.Create(new DrivePublishingOptions
        {
            StorePath = Path.Combine(root, "drive-publishing"),
        });
        return new DrivePublishingStore(
            options,
            new TestEnv(root),
            TimeProvider.System,
            NullLogger<DrivePublishingStore>.Instance);
    }

    private sealed record PublishResult(
        HttpStatusCode Status,
        string[] Outcomes,
        string Body,
        int SeasonNumber,
        string Phase,
        int? GlobalStage)
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

    private static async Task SeedConnectionAsync(WebApplicationFactory<Program> factory)
    {
        DrivePublishingStore store = factory.Services.GetRequiredService<DrivePublishingStore>();
        await store.SaveConnectionAsync(new DrivePublishingStore.ConnectionDocument(
            "refresh-1", null, DriveScopes.DriveFile, DateTimeOffset.UtcNow,
            "access-seed", DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task PutDestinationAsync(HttpClient client, Guid saveId, string json)
    {
        using HttpResponseMessage response = await client.PutAsync(
            $"/api/saves/{saveId:D}/reports/drive/destination",
            new StringContent(json, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<PublishResult> PostPublishAsync(HttpClient client, Guid saveId, bool allowCreateMissing = false)
    {
        string body = JsonSerializer.Serialize(new { confirmCrossSave = false, allowOverwriteEditedRemote = false, allowCreateMissing });
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/reports/drive/publish",
            new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new PublishResult(response.StatusCode, [], text, 0, string.Empty, null);
        }

        using JsonDocument json = JsonDocument.Parse(text);
        List<string> outcomes = json.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => $"{f.GetProperty("report").GetString()}:{f.GetProperty("outcome").GetString()}")
            .ToList();
        int season = json.RootElement.TryGetProperty("seasonNumber", out JsonElement seasonEl) && seasonEl.ValueKind == JsonValueKind.Number
            ? seasonEl.GetInt32()
            : json.RootElement.TryGetProperty("season", out JsonElement seasonAlt) && seasonAlt.ValueKind == JsonValueKind.Number
                ? seasonAlt.GetInt32()
                : 0;
        // Publish response uses camelCase via JSON options: seasonNumber/phase/globalStage may vary.
        // Fall back to parsing with case-insensitive search.
        if (season == 0)
        {
            foreach (JsonProperty prop in json.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "seasonNumber", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.Number)
                {
                    season = prop.Value.GetInt32();
                }
            }
        }

        string phase = string.Empty;
        int? globalStage = null;
        foreach (JsonProperty prop in json.RootElement.EnumerateObject())
        {
            if (string.Equals(prop.Name, "phase", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
            {
                phase = prop.Value.GetString() ?? string.Empty;
            }

            if (string.Equals(prop.Name, "globalStage", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Number)
            {
                globalStage = prop.Value.GetInt32();
            }
        }

        return new PublishResult(response.StatusCode, [.. outcomes], text, season, phase, globalStage);
    }

    private static (WebApplicationFactory<Program> Factory, string Root, FakeOAuthClient OAuth, FakeDriveClient Drive) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-autopub-corr-" + Guid.NewGuid().ToString("N"));
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

        private int _nextId;

        private static long _stamp;

        public Task<DriveFile?> GetFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default)
        {
            Files.TryGetValue(fileId, out FakeFile? file);
            return Task.FromResult<DriveFile?>(file?.ToDriveFile());
        }

        public Task<string?> DownloadFileAsync(string accessToken, string fileId, CancellationToken cancellationToken = default)
        {
            Files.TryGetValue(fileId, out FakeFile? file);
            if (file is not null
                && string.Equals(file.MimeType, "application/vnd.google-apps.folder", StringComparison.Ordinal))
            {
                throw new DriveApiException(HttpStatusCode.Forbidden, "folders are not downloadable");
            }

            return Task.FromResult(file?.Content);
        }

        public Task<IReadOnlyList<DriveFile>> ListByNameInFolderAsync(
            string accessToken,
            string folderId,
            string name,
            CancellationToken cancellationToken = default)
        {
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
            FakeFile file = Files[fileId];
            FakeFile updated = file with { Content = content, Name = newName ?? file.Name, ModifiedTime = Stamp() };
            Files[fileId] = updated;
            return Task.FromResult(updated.ToDriveFile());
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

    private sealed class TestEnv(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new NotSupportedException("Signal wait must not create scopes.");
    }
}
