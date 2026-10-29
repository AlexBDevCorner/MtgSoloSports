using Microsoft.Extensions.Logging;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Toggles opt-in automatic publishing after each completed global stage.
/// Off by default. Enabling records the current durable checkpoint as the
/// requested state so the worker catches up without waiting for the next
/// stage; disabling cancels pending automatic work without deleting already
/// published Drive files. Preserves destination ids/names and last-published
/// fingerprints. Offline-safe: no Google calls.
/// </summary>
public sealed class PutDriveAutoPublishHandler
{
    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly DriveAutoPublishTracker _tracker;
    private readonly GetSeasonStatusHandler _status;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PutDriveAutoPublishHandler> _logger;

    public PutDriveAutoPublishHandler(
        SaveStore saves,
        DrivePublishingStore publishing,
        DriveAutoPublishTracker tracker,
        GetSeasonStatusHandler status,
        TimeProvider timeProvider,
        ILogger<PutDriveAutoPublishHandler> logger)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DriveDestinationResponse> HandleAsync(
        Guid saveId,
        PutDriveAutoPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = DriveSaveGates.ForSave(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await HandleLockedAsync(saveId, request.Enabled, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DriveDestinationResponse> HandleLockedAsync(
        Guid saveId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        SaveStore.SaveDetailRecord detail = await LoadSaveAsync(saveId, cancellationToken).ConfigureAwait(false);
        DrivePublishingStore.DestinationDocument? existing =
            await _publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);

        DrivePublishingStore.DestinationDocument updated = enabled
            ? await BuildEnabledAsync(saveId, detail, existing, cancellationToken).ConfigureAwait(false)
            : await BuildDisabledAsync(detail, existing, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Drive auto-publish for save {SaveId} set to {Enabled}.",
            saveId,
            enabled);

        return await ToResponseAsync(detail, updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SaveStore.SaveDetailRecord> LoadSaveAsync(Guid saveId, CancellationToken cancellationToken)
    {
        await _saves.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await _saves.OpenAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        catch (SaveNotFoundException)
        {
            throw;
        }
    }

    private async Task<DrivePublishingStore.DestinationDocument> BuildDisabledAsync(
        SaveStore.SaveDetailRecord detail,
        DrivePublishingStore.DestinationDocument? existing,
        CancellationToken cancellationToken)
    {
        DrivePublishingStore.DestinationDocument updated = (existing ?? Blank(detail)) with
        {
            SaveName = detail.Name,
            AutoPublishEnabled = false,
            AutoRequestedSeason = null,
            AutoRequestedGlobalStage = null,
            AutoRequestedPhase = null,
            AutoRequestedUtc = null,
            AutoAttemptCount = 0,
            AutoNextRetryUtc = null,
            AutoLastError = null,
            AutoPausedForAuth = false,
        };
        await _publishing.SaveDestinationAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<DrivePublishingStore.DestinationDocument> BuildEnabledAsync(
        Guid saveId,
        SaveStore.SaveDetailRecord detail,
        DrivePublishingStore.DestinationDocument? existing,
        CancellationToken cancellationToken)
    {
        (int season, int stage, string phase) = await LoadCheckpointAsync(saveId, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DrivePublishingStore.DestinationDocument updated = (existing ?? Blank(detail)) with
        {
            SaveName = detail.Name,
            AutoPublishEnabled = true,
            AutoRequestedSeason = season,
            AutoRequestedGlobalStage = stage,
            AutoRequestedPhase = phase,
            AutoRequestedUtc = now,
            AutoAttemptCount = 0,
            AutoNextRetryUtc = now,
            AutoLastError = null,
            AutoPausedForAuth = false,
        };
        await _publishing.SaveDestinationAsync(updated, cancellationToken).ConfigureAwait(false);
        _tracker.Signal(saveId);
        return updated;
    }

    private static DrivePublishingStore.DestinationDocument Blank(SaveStore.SaveDetailRecord detail)
    {
        return new DrivePublishingStore.DestinationDocument(
            detail.SaveId,
            detail.Name,
            null, null, null, null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null);
    }

    private async Task<DriveDestinationResponse> ToResponseAsync(
        SaveStore.SaveDetailRecord detail,
        DrivePublishingStore.DestinationDocument updated,
        CancellationToken cancellationToken)
    {
        DrivePublishingStore.ConnectionDocument? connection =
            await _publishing.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        bool connected = connection is not null && !string.IsNullOrWhiteSpace(connection.RefreshToken);
        string autoStatus = DriveAutoPublishStatus.Compute(
            updated, connected, _tracker.IsPublishing(updated.SaveId));
        (string? dossierId, string? dossierName, _, _) =
            DrivePublishingStore.CurrentDossierView(updated, detail.CurrentSeason);

        // The destination endpoint shape is reused so the toggle response can
        // refresh the Reports form without a second round trip.
        return new DriveDestinationResponse(
            updated.SaveId,
            updated.SaveName,
            updated.MtshistoryFolderId,
            updated.MtshistoryFolderName,
            updated.SeasonsFolderId,
            dossierId,
            dossierName,
            updated.AlmanacFileId,
            updated.AlmanacFileName,
            null,
            null,
            updated.LastPublishedUtc,
            updated.LastSeasonNumber,
            updated.LastPhase,
            updated.LastGlobalStage,
            updated.LastError,
            updated.AutoPublishEnabled,
            autoStatus,
            updated.AutoRequestedSeason,
            updated.AutoRequestedGlobalStage,
            updated.AutoRequestedPhase);
    }

    private async Task<(int Season, int Stage, string Phase)> LoadCheckpointAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        // Read the durable checkpoint without holding the sporting lock: the
        // toggle runs outside simulation transactions and only needs the
        // latest committed read model.
        GetSeasonStatusResponse response = await _status.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
        return (response.CurrentSeasonNumber, response.GlobalStage, response.ComputedPhase);
    }
}
