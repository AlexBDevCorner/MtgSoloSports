using Microsoft.Extensions.Logging;
using MtgSoloSports.Features.Saves;
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
        // Checkpoint notes use the store's brief file gate only (never the
        // per-save gate held across slow report generation and Drive network
        // writes). The atomic read-modify-write cannot lose updates and never
        // blocks on uploads; the worker reconciles via signals plus
        // EnsurePendingSnapshot self-heal and startup recovery.
        _ = cancellationToken;
        GetSeasonStatusResponse? status = await TryLoadStatusAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        if (status is null)
        {
            return;
        }

        string? saveName = await LoadSaveNameAsync(saveId, CancellationToken.None).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DrivePublishingStore.DestinationDocument? persisted =
            await _publishing.UpdateDestinationCheckpointAsync(
                saveId,
                current => MergeCheckpoint(current, status, saveName, now),
                CancellationToken.None).ConfigureAwait(false);
        if (persisted is null || !persisted.AutoPublishEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(persisted.MtshistoryFolderId))
        {
            return;
        }

        // Duplicate notes collapse (requested already equals durable state) but
        // still signal when a pending request awaits retry. Paused saves stay
        // silent until reconnect/reconfiguration.
        if (!persisted.AutoPausedForAuth && DriveAutoPublishStatus.HasPendingRequest(persisted))
        {
            _tracker.Signal(saveId);
        }
    }

    private static DrivePublishingStore.DestinationDocument? MergeCheckpoint(
        DrivePublishingStore.DestinationDocument current,
        GetSeasonStatusResponse status,
        string? saveName,
        DateTimeOffset now)
    {
        if (!current.AutoPublishEnabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(current.MtshistoryFolderId))
        {
            return null;
        }

        // Monotonic guard: checkpoint notes sample the durable sporting state
        // before taking the store file gate, so a delayed note for stage N can
        // arrive after a newer note for stage N+1. The read-modify-write is
        // atomic but cannot order the independently sampled snapshots, so
        // compare against the persisted requested checkpoint here and reject
        // duplicates and stale (older-or-equal) snapshots. Only a strictly
        // newer (season, stage, phase) checkpoint advances the request.
        if (CompareCheckpoint(current, status) <= 0)
        {
            return null;
        }

        return current with
        {
            SaveName = saveName ?? current.SaveName,
            AutoRequestedSeason = status.CurrentSeasonNumber,
            AutoRequestedGlobalStage = status.GlobalStage,
            AutoRequestedPhase = status.ComputedPhase,
            AutoRequestedUtc = now,
            AutoNextRetryUtc = current.AutoPausedForAuth ? current.AutoNextRetryUtc : now,
        };
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

    /// <summary>
    /// Total ordering for requested checkpoints: season dominates stage, stage
    /// dominates phase. Phase order follows <see cref="SavePhase"/> numeric
    /// order, which matches the lifecycle progression (SeasonInProgress through
    /// CupComplete). Returns &lt;0 when the incoming snapshot is older than the
    /// persisted request, 0 when equal, &gt;0 when strictly newer.
    /// </summary>
    internal static int CompareCheckpoint(
        DrivePublishingStore.DestinationDocument current,
        GetSeasonStatusResponse status)
    {
        if (!current.AutoRequestedSeason.HasValue)
        {
            return 1;
        }

        int seasonCmp = status.CurrentSeasonNumber.CompareTo(current.AutoRequestedSeason.Value);
        if (seasonCmp != 0)
        {
            return seasonCmp;
        }

        int currentStage = current.AutoRequestedGlobalStage ?? int.MinValue;
        int stageCmp = status.GlobalStage.CompareTo(currentStage);
        if (stageCmp != 0)
        {
            return stageCmp;
        }

        string? currentPhase = current.AutoRequestedPhase;
        if (string.IsNullOrEmpty(currentPhase))
        {
            return 1;
        }

        if (string.Equals(currentPhase, status.ComputedPhase, StringComparison.Ordinal))
        {
            return 0;
        }

        bool incomingOk = Enum.TryParse<SavePhase>(status.ComputedPhase, ignoreCase: false, out SavePhase incoming)
            && Enum.IsDefined(incoming);
        bool currentOk = Enum.TryParse<SavePhase>(currentPhase, ignoreCase: false, out SavePhase persisted)
            && Enum.IsDefined(persisted);
        if (incomingOk && currentOk)
        {
            return ((int)incoming).CompareTo((int)persisted);
        }

        return string.Compare(status.ComputedPhase, currentPhase, StringComparison.Ordinal);
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
