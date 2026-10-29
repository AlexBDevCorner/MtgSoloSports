using Microsoft.Extensions.Options;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Read-only Drive publishing
/// status for one save: operator configuration, account link, destination ids
/// and last-published fingerprints/outcomes. Never returns tokens or secrets.
/// </summary>
public sealed class GetDriveStatusHandler
{
    private readonly SaveStore _saves;
    private readonly DrivePublishingStore _publishing;
    private readonly IOptions<DrivePublishingOptions> _options;

    public GetDriveStatusHandler(
        SaveStore saves,
        DrivePublishingStore publishing,
        IOptions<DrivePublishingOptions> options)
    {
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
        _publishing = publishing ?? throw new ArgumentNullException(nameof(publishing));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DriveStatusResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
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

        DrivePublishingStore.ConnectionDocument? connection =
            await _publishing.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        DrivePublishingStore.DestinationDocument? destination =
            await _publishing.GetDestinationAsync(saveId, cancellationToken).ConfigureAwait(false);

        return new DriveStatusResponse(
            detail.SaveId,
            detail.Name,
            detail.CurrentSeason,
            detail.Phase,
            _options.Value.IsGoogleConfigured,
            connection is not null && !string.IsNullOrWhiteSpace(connection.RefreshToken),
            connection?.Scopes,
            connection?.ConnectedUtc,
            new DriveReportSlotDto(
                destination?.DossierFileId,
                destination?.DossierFileName,
                destination?.DossierFingerprint,
                destination?.DossierRemoteModifiedTime,
                destination?.LastDossierOutcome,
                null),
            new DriveReportSlotDto(
                destination?.AlmanacFileId,
                destination?.AlmanacFileName,
                destination?.AlmanacFingerprint,
                destination?.AlmanacRemoteModifiedTime,
                destination?.LastAlmanacOutcome,
                null),
            destination?.MtshistoryFolderId,
            destination?.MtshistoryFolderName,
            destination?.LastPublishedUtc,
            destination?.LastSeasonNumber,
            destination?.LastPhase,
            destination?.LastGlobalStage,
            destination?.LastError);
    }
}
