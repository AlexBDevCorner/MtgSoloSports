using Microsoft.Extensions.Logging;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Post-commit observer for durable sporting boundaries. Callers invoke
/// <see cref="NotifyStageCheckpointAsync"/> strictly after the simulation
/// transaction commits; this method performs only local reads/writes
/// (save DB read, app-level JSON write) and never touches the network, so a
/// sporting response never waits for Google Drive. Burst notifications
/// coalesce by overwriting the requested checkpoint with the latest durable
/// state. Failures are swallowed (logged) so publishing can never fail a
/// sporting command.
/// </summary>
public sealed class DriveAutoPublishNotifier
{
    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly DriveAutoPublishTracker _tracker;
    private readonly GetSeasonStatusHandler _status;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DriveAutoPublishNotifier> _logger;

    public DriveAutoPublishNotifier(
        SaveStore saves,
        DrivePublishingStore publishing,
        DriveAutoPublishTracker tracker,
        GetSeasonStatusHandler status,
        TimeProvider timeProvider,
        ILogger<DriveAutoPublishNotifier> logger)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Records the latest durable global-stage checkpoint as the requested
    /// automatic-publish state. No-ops when auto mode is disabled, no
    /// destination is configured, or the save cannot be read. Never throws.
    /// </summary>
    public async Task NotifyStageCheckpointAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            return;
        }

        // Post-commit durability: the sporting transaction already committed,
        // so this local checkpoint note must survive HTTP client disconnect or
        // cancellation. Intentionally ignore the caller's request token and use
        // an unlinked token for the gate plus local file/DB reads/writes.
        // Never throws (including OperationCanceledException) so publishing
        // can never fail a sporting command.
        _ = cancellationToken;
        try
        {
            await NotifyLockedAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Publishing is an optional side effect: sporting progression
            // must never fail because the checkpoint note could not be read.
            // No tokens or report content are logged.
            _logger.LogWarning(ex, "Drive auto-publish checkpoint note failed for save {SaveId}.", saveId);
        }
    }

    private async Task NotifyLockedAsync(Guid saveId, CancellationToken cancellationToken)
    {
        // Requirement 3: sporting responses must never wait for Google Drive.
        // The per-save gate is held across slow report generation and Drive
        // network writes by HandleAutoAsync. Take it without waiting; when an
        // upload is in flight, just signal and let the running worker
        // reconcile the durable checkpoint after the current publish settles
        // (EnsurePendingSnapshot self-heal plus startup recovery). Uses an
        // unlinked wait so client disconnect never throws here.
        _ = cancellationToken;
        SemaphoreSlim gate = DriveSaveGates.ForSave(saveId);
        bool entered = await gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false);
        if (!entered)
        {
            _tracker.Signal(saveId);
            return;
        }

        try
        {
            await NoteCheckpointLockedAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task NoteCheckpointLockedAsync(Guid saveId, CancellationToken cancellationToken)
    {
        DrivePublishingStore.DestinationDocument? destination =
            await _publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (destination is null || !destination.AutoPublishEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(destination.MtshistoryFolderId))
        {
            return;
        }

        GetSeasonStatusResponse? status = await TryLoadStatusAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return;
        }

        // Duplicate notifications for the same durable checkpoint collapse:
        // the requested state already equals the latest committed state.
        if (IsDuplicate(destination, status))
        {
            // Still signal: a prior attempt may have failed and be awaiting
            // retry, or the worker may have missed the first signal.
            if (DriveAutoPublishStatus.HasPendingRequest(destination))
            {
                _tracker.Signal(saveId);
            }

            return;
        }

        await StoreCheckpointLockedAsync(saveId, destination, status, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GetSeasonStatusResponse?> TryLoadStatusAsync(Guid saveId, CancellationToken cancellationToken)
    {
        try
        {
            return await _status.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        catch (SaveNotFoundException)
        {
            return null;
        }
    }

    private static bool IsDuplicate(
        DrivePublishingStore.DestinationDocument destination,
        GetSeasonStatusResponse status)
    {
        return destination.AutoRequestedSeason == status.CurrentSeasonNumber
            && destination.AutoRequestedGlobalStage == status.GlobalStage
            && string.Equals(destination.AutoRequestedPhase, status.ComputedPhase, StringComparison.Ordinal);
    }

    private async Task StoreCheckpointLockedAsync(
        Guid saveId,
        DrivePublishingStore.DestinationDocument destination,
        GetSeasonStatusResponse status,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DrivePublishingStore.DestinationDocument updated = destination with
        {
            SaveName = (await LoadSaveNameAsync(saveId, cancellationToken).ConfigureAwait(false)) ?? destination.SaveName,
            AutoRequestedSeason = status.CurrentSeasonNumber,
            AutoRequestedGlobalStage = status.GlobalStage,
            AutoRequestedPhase = status.ComputedPhase,
            AutoRequestedUtc = now,
            AutoNextRetryUtc = destination.AutoPausedForAuth ? destination.AutoNextRetryUtc : now,
        };
        await _publishing.SaveDestinationAsync(updated, cancellationToken).ConfigureAwait(false);
        if (!updated.AutoPausedForAuth)
        {
            _tracker.Signal(saveId);
        }
    }

    private async Task<string?> LoadSaveNameAsync(Guid saveId, CancellationToken cancellationToken)
    {
        try
        {
            SaveStore.SaveDetailRecord detail = await _saves.OpenAsync(saveId, cancellationToken).ConfigureAwait(false);
            return detail.Name;
        }
        catch (SaveNotFoundException)
        {
            return null;
        }
    }
}
