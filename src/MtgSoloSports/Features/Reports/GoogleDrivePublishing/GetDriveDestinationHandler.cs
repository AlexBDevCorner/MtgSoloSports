using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Reads the per-save Drive destination mapping (folder/file ids, adopted
/// custom names, fingerprints, last publish). Offline-safe: no Google calls.
/// </summary>
public sealed class GetDriveDestinationHandler
{
    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly DriveAutoPublishTracker _tracker;

    public GetDriveDestinationHandler(SaveStore saves, DrivePublishingStore publishing, DriveAutoPublishTracker? tracker = null)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _tracker = tracker ?? new DriveAutoPublishTracker();
    }

    public async Task<DriveDestinationResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        await _saves.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        SaveStore.SaveDetailRecord detail;
        try
        {
            detail = await _saves.OpenAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        catch (SaveNotFoundException)
        {
            throw;
        }

        DrivePublishingStore.DestinationDocument? destination =
            await _publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);
        DrivePublishingStore.ConnectionDocument? connection =
            await _publishing.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        bool connected = connection is not null && !string.IsNullOrWhiteSpace(connection.RefreshToken);

        (string? dossierId, string? dossierName, string? dossierFingerprint, _) =
            DrivePublishingStore.CurrentDossierView(destination, detail.CurrentSeason);

        string autoStatus = DriveAutoPublishStatus.Compute(
            destination,
            connected,
            destination is not null && _tracker.IsPublishing(detail.SaveId));

        return new DriveDestinationResponse(
            detail.SaveId,
            detail.Name,
            destination?.MtshistoryFolderId,
            destination?.MtshistoryFolderName,
            destination?.SeasonsFolderId,
            dossierId,
            dossierName,
            destination?.AlmanacFileId,
            destination?.AlmanacFileName,
            dossierFingerprint,
            destination?.AlmanacFingerprint,
            destination?.LastPublishedUtc,
            destination?.LastSeasonNumber,
            destination?.LastPhase,
            destination?.LastGlobalStage,
            destination?.LastError,
            destination?.AutoPublishEnabled ?? false,
            autoStatus,
            destination?.AutoRequestedSeason,
            destination?.AutoRequestedGlobalStage,
            destination?.AutoRequestedPhase);
    }
}
