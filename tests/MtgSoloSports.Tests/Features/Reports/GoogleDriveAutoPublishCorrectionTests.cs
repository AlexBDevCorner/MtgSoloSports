using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
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
