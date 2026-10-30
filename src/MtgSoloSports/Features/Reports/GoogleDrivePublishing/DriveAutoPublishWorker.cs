using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Single-process automatic-publish pump. Consumes coalesced post-commit
/// signals and bounded retry timers, then publishes the latest stable snapshot
/// through the MSS-089 publisher. Serialized per save via the shared
/// destination gate (inside <see cref="PublishDriveReportsHandler"/>);
/// concurrent signals for the same save collapse to one publish of the newest
/// durable state, never an older report overwriting a newer one as the final
/// state. Network failures never touch sporting state; permanent auth errors
/// pause automatic work until reconnect. No tokens or report content are logged.
/// </summary>
public sealed class DriveAutoPublishWorker : BackgroundService
{
    internal static TimeSpan SignalPollInterval = TimeSpan.FromSeconds(5);
    internal const int MaxAttempts = 8;

    private readonly IServiceScopeFactory _scopes;
    private readonly DriveAutoPublishTracker _tracker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DriveAutoPublishWorker> _logger;

    public DriveAutoPublishWorker(
        IServiceScopeFactory scopes,
        DriveAutoPublishTracker tracker,
        TimeProvider timeProvider,
        ILogger<DriveAutoPublishWorker> logger)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnqueuePendingOnStartupAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PumpOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Drive auto-publish pump iteration failed; continuing.");
                await DelayQuietAsync(SignalPollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PumpOnceAsync(CancellationToken stoppingToken)
    {
        Guid? signaled = await WaitForSignalAsync(stoppingToken).ConfigureAwait(false);
        if (signaled.HasValue)
        {
            // Coalesce bursts: drain every queued signal so dozens of rapid
            // stages collapse to one publish per save of the newest durable
            // state, never an older report overwriting a newer one.
            HashSet<Guid> pending = new() { signaled.Value };
            while (_tracker.Reader.TryRead(out Guid extra))
            {
                pending.Add(extra);
            }

            foreach (Guid saveId in pending)
            {
                stoppingToken.ThrowIfCancellationRequested();
                await ProcessSaveAsync(saveId, stoppingToken).ConfigureAwait(false);
            }
        }
        else
        {
            await ProcessDueRetriesAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<Guid?> WaitForSignalAsync(CancellationToken stoppingToken)
    {
        if (_tracker.Reader.TryRead(out Guid immediate))
        {
            return immediate;
        }

        // Await the signal with a cancellable timeout. The timeout token is
        // disposed here, so no waiter is left registered on the channel when
        // the poll interval elapses. Never use Task.WhenAny over ReadAsync:
        // the losing read would stay queued and steal the next signal.
        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(SignalPollInterval);
        try
        {
            bool available = await _tracker.Reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!available)
            {
                return null;
            }

            if (_tracker.Reader.TryRead(out Guid signaled))
            {
                return signaled;
            }

            return null;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task EnqueuePendingOnStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            DrivePublishingStore publishing = scope.ServiceProvider.GetRequiredService<DrivePublishingStore>();
            IReadOnlyList<DrivePublishingStore.DestinationDocument> all =
                await publishing.ListDestinationsAsync(cancellationToken).ConfigureAwait(false);
            foreach (DrivePublishingStore.DestinationDocument destination in all)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RecoverOneAsync(publishing, destination, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drive auto-publish startup scan failed; signals will arrive on new stages.");
        }
    }

    private async Task RecoverOneAsync(
        DrivePublishingStore publishing,
        DrivePublishingStore.DestinationDocument destination,
        CancellationToken cancellationToken)
    {
        if (!destination.AutoPublishEnabled)
        {
            return;
        }

        // Startup reconciliation: if a stage committed but the post-commit
        // note never persisted (cancelled HTTP token or crash between commit
        // and note), persist it now from the durable save state. The notifier
        // is unlinked from cancellation and no-ops when disabled,
        // unconfigured or already caught up. A persisted permission pause
        // (e.g. HTTP 403 grant failure) stays paused across restarts: only an
        // explicit reconnect/reconfiguration may unpause, never a restart
        // with the same unchanged grant.
        await ReconcileDurableCheckpointAsync(destination.SaveId, cancellationToken).ConfigureAwait(false);

        DrivePublishingStore.DestinationDocument? current;
        try
        {
            current = await publishing.GetDestinationAsync(destination.SaveId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Drive auto-publish startup re-read failed for save {SaveId}.", destination.SaveId);
            return;
        }

        if (current is null || !current.AutoPublishEnabled || !DriveAutoPublishStatus.HasPendingRequest(current))
        {
            return;
        }

        if (current.AutoPausedForAuth)
        {
            return;
        }

        _tracker.Signal(current.SaveId);
    }

    private async Task ReconcileDurableCheckpointAsync(Guid saveId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            DriveAutoPublishNotifier? notifier =
                scope.ServiceProvider.GetService<DriveAutoPublishNotifier>();
            if (notifier is null)
            {
                return;
            }

            await notifier.NotifyStageCheckpointAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drive auto-publish startup reconcile failed for save {SaveId}.", saveId);
        }
    }

    private async Task ProcessDueRetriesAsync(CancellationToken cancellationToken)
    {
        List<Guid> due;
        try
        {
            due = await CollectDueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drive auto-publish retry scan failed.");
            return;
        }

        foreach (Guid saveId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessSaveAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<List<Guid>> CollectDueAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        DrivePublishingStore publishing = scope.ServiceProvider.GetRequiredService<DrivePublishingStore>();
        if (!await HasConnectionAsync(publishing, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        IReadOnlyList<DrivePublishingStore.DestinationDocument> all =
            await publishing.ListDestinationsAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<Guid> due = [];
        foreach (DrivePublishingStore.DestinationDocument destination in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDue(destination, now))
            {
                due.Add(destination.SaveId);
            }
        }

        return due;
    }

    private static bool IsDue(DrivePublishingStore.DestinationDocument destination, DateTimeOffset now)
    {
        return destination.AutoPublishEnabled
            && !destination.AutoPausedForAuth
            && destination.AutoNextRetryUtc.HasValue
            && destination.AutoNextRetryUtc.Value <= now
            && DriveAutoPublishStatus.HasPendingRequest(destination);
    }

    private static async Task<bool> HasConnectionAsync(
        DrivePublishingStore publishing,
        CancellationToken cancellationToken)
    {
        DrivePublishingStore.ConnectionDocument? connection =
            await publishing.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        return connection is not null && !string.IsNullOrWhiteSpace(connection.RefreshToken);
    }

    internal async Task ProcessSaveAsync(Guid saveId, CancellationToken cancellationToken)
    {
        if (saveId == Guid.Empty)
        {
            return;
        }

        DrivePublishingStore.DestinationDocument? snapshot =
            await EnsurePendingSnapshotAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        if (snapshot.AutoPausedForAuth)
        {
            return;
        }

        if (snapshot.AutoNextRetryUtc.HasValue && snapshot.AutoNextRetryUtc.Value > _timeProvider.GetUtcNow())
        {
            return;
        }

        if (!await HasSaveConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.MtshistoryFolderId))
        {
            await MarkFailedAsync(saveId, "No Drive destination is configured. Set the mtshistory folder id in destination settings, then run Publish now once.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunPublishAttemptAsync(saveId, snapshot, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPublishAttemptAsync(
        Guid saveId,
        DrivePublishingStore.DestinationDocument snapshot,
        CancellationToken cancellationToken)
    {
        _tracker.SetPublishing(saveId);
        DateTimeOffset started = _timeProvider.GetUtcNow();
        try
        {
            PublishDriveReportsResponse? response = await PublishLatestAsync(saveId, snapshot, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                // Auto mode was disabled under the per-save gate after the
                // snapshot was taken. No Drive work started; skip quietly.
                return;
            }

            await FinishAfterPublishAsync(saveId, response, started, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await HandlePublishExceptionAsync(saveId, ex, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _tracker.ClearPublishing(saveId);
        }
    }

    private async Task<DrivePublishingStore.DestinationDocument?> EnsurePendingSnapshotAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        DrivePublishingStore.DestinationDocument? snapshot =
            await LoadSnapshotAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || !snapshot.AutoPublishEnabled)
        {
            return null;
        }

        if (DriveAutoPublishStatus.HasPendingRequest(snapshot))
        {
            return snapshot;
        }

        // Signal-triggered self-heal: a missing post-commit note (cancelled
        // bulk, crash between commit and note) leaves no pending request
        // while the durable save is ahead. Reconcile now so this save does
        // not wait for a restart. Duplicate-safe; no signal when caught up.
        await ReconcileDurableCheckpointAsync(saveId, cancellationToken).ConfigureAwait(false);
        snapshot = await LoadSnapshotAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || !snapshot.AutoPublishEnabled)
        {
            return null;
        }

        if (!DriveAutoPublishStatus.HasPendingRequest(snapshot))
        {
            await ClearStaleErrorAsync(saveId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return snapshot;
    }

    private async Task<bool> HasSaveConnectionAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        DrivePublishingStore publishing = scope.ServiceProvider.GetRequiredService<DrivePublishingStore>();
        return await HasConnectionAsync(publishing, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DrivePublishingStore.DestinationDocument?> LoadSnapshotAsync(Guid saveId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        DrivePublishingStore publishing = scope.ServiceProvider.GetRequiredService<DrivePublishingStore>();
        return await publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PublishDriveReportsResponse?> PublishLatestAsync(
        Guid saveId,
        DrivePublishingStore.DestinationDocument snapshot,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        PublishDriveReportsHandler publisher =
            scope.ServiceProvider.GetRequiredService<PublishDriveReportsHandler>();
        // Existing hierarchy updates in place; a known Seasons/ folder
        // allows new stable season files to be created automatically, while
        // an unknown Seasons/ folder stays fail-closed (no duplicate) until
        // the user runs Publish now once with creation confirmed. The auto
        // entry point re-checks AutoPublishEnabled atomically under the
        // per-save gate so a concurrent disable cannot start a new upload.
        bool allowCreateMissing = !string.IsNullOrWhiteSpace(snapshot.SeasonsFolderId);
        return await publisher.HandleAutoAsync(
            saveId,
            new PublishDriveReportsRequest(
                ConfirmCrossSave: false,
                AllowOverwriteEditedRemote: false,
                AllowCreateMissing: allowCreateMissing),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePublishExceptionAsync(Guid saveId, Exception ex, CancellationToken cancellationToken)
    {
        if (ex is DriveNotConnectedException notConnected)
        {
            await MarkAuthPausedAsync(saveId, Trim(notConnected.Message), cancellationToken).ConfigureAwait(false);
        }
        else if (ex is DriveApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
        {
            await MarkAuthPausedAsync(saveId, "Google authorization expired or was revoked. Reconnect with Connect Google Drive.", cancellationToken).ConfigureAwait(false);
        }
        else if (ex is DriveApiException { StatusCode: System.Net.HttpStatusCode.Forbidden })
        {
            // Permanent permission failure (missing drive.file grant): pause
            // automatic work until reconnect/reconfiguration instead of
            // retrying an unauthorized upload on every later stage.
            await MarkAuthPausedAsync(saveId, Trim(ex.Message), cancellationToken).ConfigureAwait(false);
        }
        else if (IsPermissionMessage(ex.Message))
        {
            // Publisher surfaces some 403 grant failures as
            // DriveConflictException (e.g. inaccessible Seasons/ folder).
            // Those are also permanent permission errors, not retryable
            // failures.
            await MarkAuthPausedAsync(saveId, Trim(ex.Message), cancellationToken).ConfigureAwait(false);
        }
        else if (ex is DriveNotConfiguredException or DriveConflictException or InvalidOperationException)
        {
            await MarkFailedAsync(saveId, Trim(ex.Message), cancellationToken).ConfigureAwait(false);
        }
        else if (ex is DriveApiException { IsTransient: true } or DriveUpstreamException or HttpRequestException or TimeoutException)
        {
            await MarkTransientAsync(saveId, Trim(ex.Message), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await MarkFailedAsync(saveId, Trim(ex.Message), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FinishAfterPublishAsync(
        Guid saveId,
        PublishDriveReportsResponse response,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        DrivePublishingStore publishing = scope.ServiceProvider.GetRequiredService<DrivePublishingStore>();
        SemaphoreSlim gate = DriveSaveGates.ForSave(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SettleLockedAsync(publishing, saveId, response, started, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SettleLockedAsync(
        DrivePublishingStore publishing,
        Guid saveId,
        PublishDriveReportsResponse response,
        DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        DrivePublishingStore.DestinationDocument? current =
            await publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (current is null || !current.AutoPublishEnabled)
        {
            return;
        }

        IReadOnlyList<DriveFileOutcome> failures = response.Files
            .Where(f => string.Equals(f.Outcome, "failed", StringComparison.Ordinal))
            .ToList();
        if (failures.Count == 0)
        {
            await SettleSuccessLockedAsync(publishing, current, saveId, response, cancellationToken).ConfigureAwait(false);
            return;
        }

        string detail = failures[0].Detail ?? $"{failures[0].Report} publish failed.";
        if (IsPermissionDetail(detail))
        {
            // Permanent permission failure (missing drive.file grant, 403):
            // pause automatic work until reconnect/reconfiguration instead of
            // retrying an unauthorized upload on every later stage. Takes
            // precedence over the stale-mid-publish retry: the newest state
            // would fail the same way.
            await publishing.SaveDestinationAsync(current with
            {
                AutoLastError = Trim(detail),
                AutoNextRetryUtc = null,
                AutoPausedForAuth = true,
            }, cancellationToken).ConfigureAwait(false);
        }
        else if (IsTransientDetail(detail) || IsStaleMidPublish(current, started))
        {
            await SaveTransientLockedAsync(publishing, current, detail, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await publishing.SaveDestinationAsync(current with
            {
                AutoLastError = Trim(detail),
                AutoNextRetryUtc = null,
            }, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("Drive auto-publish for save {SaveId} reported {Outcome}.", saveId, failures[0].Outcome);
    }

    private async Task SettleSuccessLockedAsync(
        DrivePublishingStore publishing,
        DrivePublishingStore.DestinationDocument current,
        Guid saveId,
        PublishDriveReportsResponse response,
        CancellationToken cancellationToken)
    {
        // The publisher persisted Last* to the latest stable snapshot. When a
        // newer sporting checkpoint landed mid-publish the requested state is
        // newer than what was just uploaded, so schedule an immediate
        // follow-up instead of reporting success.
        if (DriveAutoPublishStatus.IsCaughtUp(current))
        {
            await publishing.SaveDestinationAsync(current with
            {
                AutoLastError = null,
                AutoAttemptCount = 0,
                AutoNextRetryUtc = null,
                AutoPausedForAuth = false,
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await publishing.SaveDestinationAsync(current with
            {
                AutoNextRetryUtc = _timeProvider.GetUtcNow(),
            }, cancellationToken).ConfigureAwait(false);
            _tracker.Signal(saveId);
        }

        _logger.LogInformation(
            "Drive auto-publish for save {SaveId} settled season {Season} stage {Stage}.",
            saveId,
            response.SeasonNumber,
            response.GlobalStage);
    }

    private bool IsStaleMidPublish(DrivePublishingStore.DestinationDocument current, DateTimeOffset started)
    {
        // A requested checkpoint newer than this attempt's start means sporting
        // progress landed mid-publish; the just-uploaded snapshot is stale, so
        // treat even a permanent-looking outcome as retryable once the newest
        // state is uploaded. The newest state's own outcome decides.
        return current.AutoRequestedUtc.HasValue && current.AutoRequestedUtc.Value > started;
    }

    private async Task ClearStaleErrorAsync(Guid saveId, CancellationToken cancellationToken)
    {
        await WithDestinationAsync(saveId, static (publishing, current, ct) =>
        {
            if (DriveAutoPublishStatus.IsCaughtUp(current) && HasErrorState(current))
            {
                return publishing.SaveDestinationAsync(current with
                {
                    AutoLastError = null,
                    AutoAttemptCount = 0,
                    AutoNextRetryUtc = null,
                    AutoPausedForAuth = false,
                }, ct);
            }

            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasErrorState(DrivePublishingStore.DestinationDocument current)
    {
        return !string.IsNullOrWhiteSpace(current.AutoLastError)
            || current.AutoAttemptCount != 0
            || current.AutoNextRetryUtc.HasValue
            || current.AutoPausedForAuth;
    }

    private async Task MarkTransientAsync(Guid saveId, string message, CancellationToken cancellationToken)
    {
        await WithDestinationAsync(saveId, (publishing, current, ct) =>
            SaveTransientLockedAsync(publishing, current, message, ct), cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Drive auto-publish for save {SaveId} will retry: {Message}.", saveId, message);
    }

    private async Task SaveTransientLockedAsync(
        DrivePublishingStore publishing,
        DrivePublishingStore.DestinationDocument current,
        string message,
        CancellationToken cancellationToken)
    {
        int attempts = checked(current.AutoAttemptCount + 1);
        if (attempts >= MaxAttempts)
        {
            await publishing.SaveDestinationAsync(current with
            {
                AutoLastError = Trim(message),
                AutoAttemptCount = attempts,
                AutoNextRetryUtc = null,
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await publishing.SaveDestinationAsync(current with
        {
            AutoLastError = Trim(message),
            AutoAttemptCount = attempts,
            AutoNextRetryUtc = _timeProvider.GetUtcNow().Add(NextRetryDelay(attempts)),
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkFailedAsync(Guid saveId, string message, CancellationToken cancellationToken)
    {
        await WithDestinationAsync(saveId, (publishing, current, ct) =>
            publishing.SaveDestinationAsync(current with
            {
                AutoLastError = Trim(message),
                AutoNextRetryUtc = null,
                AutoPausedForAuth = false,
            }, ct), cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Drive auto-publish for save {SaveId} failed: {Message}.", saveId, message);
    }

    private async Task MarkAuthPausedAsync(Guid saveId, string message, CancellationToken cancellationToken)
    {
        await WithDestinationAsync(saveId, (publishing, current, ct) =>
            publishing.SaveDestinationAsync(current with
            {
                AutoLastError = Trim(message),
                AutoNextRetryUtc = null,
                AutoPausedForAuth = true,
            }, ct), cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Drive auto-publish for save {SaveId} paused (auth): {Message}.", saveId, message);
    }

    private async Task WithDestinationAsync(
        Guid saveId,
        Func<DrivePublishingStore, DrivePublishingStore.DestinationDocument, CancellationToken, Task> update,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        DrivePublishingStore publishing = scope.ServiceProvider.GetRequiredService<DrivePublishingStore>();
        SemaphoreSlim gate = DriveSaveGates.ForSave(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DrivePublishingStore.DestinationDocument? current =
                await publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);
            if (current is null || !current.AutoPublishEnabled)
            {
                return;
            }

            await update(publishing, current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal static TimeSpan NextRetryDelay(int attempt)
    {
        return attempt switch
        {
            <= 1 => TimeSpan.FromSeconds(5),
            2 => TimeSpan.FromSeconds(15),
            3 => TimeSpan.FromMinutes(1),
            _ => TimeSpan.FromMinutes(5),
        };
    }

    internal static bool IsTransientDetail(string detail)
    {
        return detail.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("could not be confirmed", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("does not match the new report", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Re-run Publish now to retry", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPermissionDetail(string detail)
    {
        // All publisher permission failures embed the HTTP status as
        // "(403)" (denied folder/file/grant). Manual-publish guidance uses
        // the same marker, so auto-publish can reliably pause on it.
        return detail.Contains("(403)", StringComparison.Ordinal);
    }

    private static bool IsPermissionMessage(string message)
    {
        return message.Contains("(403)", StringComparison.Ordinal);
    }

    private static async Task DelayQuietAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Trim(string message)
    {
        string trimmed = message.Trim();
        return trimmed.Length > 300 ? trimmed[..300] : trimmed;
    }
}
