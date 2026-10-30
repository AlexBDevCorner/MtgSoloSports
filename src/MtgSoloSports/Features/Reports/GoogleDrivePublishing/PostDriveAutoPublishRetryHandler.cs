using Microsoft.Extensions.Logging;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Manual retry/catch-up for automatic publishing: re-signals the background
/// pump for the latest durable checkpoint without changing files. Manual
/// <c>Publish now</c> remains the override that uploads immediately with
/// explicit overwrite/creation confirmations.
/// </summary>
public sealed class PostDriveAutoPublishRetryHandler
{
    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly DriveAutoPublishTracker _tracker;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PostDriveAutoPublishRetryHandler> _logger;

    public PostDriveAutoPublishRetryHandler(
        SaveStore saves,
        DrivePublishingStore publishing,
        DriveAutoPublishTracker tracker,
        TimeProvider timeProvider,
        ILogger<PostDriveAutoPublishRetryHandler> logger)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DriveDestinationResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SaveStore.SaveDetailRecord detail = await LoadSaveAsync(saveId, cancellationToken).ConfigureAwait(false);

        SemaphoreSlim gate = DriveSaveGates.ForSave(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await HandleLockedAsync(detail, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
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

    private async Task<DriveDestinationResponse> HandleLockedAsync(
        SaveStore.SaveDetailRecord detail,
        CancellationToken cancellationToken)
    {
        DrivePublishingStore.DestinationDocument? destination =
            await _publishing.GetDestinationAsync(detail.SaveId, cancellationToken).ConfigureAwait(false);
        if (destination is null || !destination.AutoPublishEnabled)
        {
            throw new DriveConflictException(
                "Automatic publishing is disabled for this save. Enable it in Reports/Drive settings first.");
        }

        await RequireConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (!DriveAutoPublishStatus.HasPendingRequest(destination))
        {
            // Still signal once: the worker clears stale error state when
            // the requested checkpoint already matches the last publish.
            _tracker.Signal(detail.SaveId);
            return await ToResponseAsync(detail, destination, cancellationToken).ConfigureAwait(false);
        }

        if (destination.AutoPausedForAuth)
        {
            throw new DriveNotConnectedException(
                "Automatic publishing is paused after an authorization failure. Reconnect with Connect Google Drive, then retry.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DrivePublishingStore.DestinationDocument updated = destination with
        {
            AutoNextRetryUtc = now,
        };
        await _publishing.SaveDestinationAsync(updated, cancellationToken).ConfigureAwait(false);
        _tracker.Signal(detail.SaveId);
        _logger.LogInformation("Drive auto-publish retry signalled for save {SaveId}.", detail.SaveId);
        return await ToResponseAsync(detail, updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireConnectionAsync(CancellationToken cancellationToken)
    {
        DrivePublishingStore.ConnectionDocument? connection =
            await _publishing.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null || string.IsNullOrWhiteSpace(connection.RefreshToken))
        {
            throw new DriveNotConnectedException(
                "No Google account is connected. Use Connect Google Drive first.");
        }
    }

    private async Task<DriveDestinationResponse> ToResponseAsync(
        SaveStore.SaveDetailRecord detail,
        DrivePublishingStore.DestinationDocument destination,
        CancellationToken cancellationToken)
    {
        DrivePublishingStore.ConnectionDocument? connection =
            await _publishing.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        bool connected = connection is not null && !string.IsNullOrWhiteSpace(connection.RefreshToken);
        string autoStatus = DriveAutoPublishStatus.Compute(
            destination, connected, _tracker.IsPublishing(detail.SaveId));
        (string? dossierId, string? dossierName, _, _) =
            DrivePublishingStore.CurrentDossierView(destination, detail.CurrentSeason);
        return new DriveDestinationResponse(
            destination.SaveId,
            destination.SaveName,
            destination.MtshistoryFolderId,
            destination.MtshistoryFolderName,
            destination.SeasonsFolderId,
            dossierId,
            dossierName,
            destination.AlmanacFileId,
            destination.AlmanacFileName,
            null,
            null,
            destination.LastPublishedUtc,
            destination.LastSeasonNumber,
            destination.LastPhase,
            destination.LastGlobalStage,
            destination.LastError,
            destination.AutoPublishEnabled,
            autoStatus,
            destination.AutoRequestedSeason,
            destination.AutoRequestedGlobalStage,
            destination.AutoRequestedPhase);
    }
}
