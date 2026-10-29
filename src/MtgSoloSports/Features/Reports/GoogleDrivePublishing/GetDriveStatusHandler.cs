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

        return BuildResponse(detail, connection, destination);
    }

    private DriveStatusResponse BuildResponse(
        SaveStore.SaveDetailRecord detail,
        DrivePublishingStore.ConnectionDocument? connection,
        DrivePublishingStore.DestinationDocument? destination)
    {
        (string? dossierId, string? dossierName, string? dossierFingerprint, string? dossierModified) =
            DrivePublishingStore.CurrentDossierView(destination, detail.CurrentSeason);

        // Picker credentials are public browser credentials (OAuth client id,
        // Picker browser API key, Cloud project number for setAppId). The
        // client secret and tokens are never returned. PickerEnabled gates the
        // Choose-from-Drive grant flow; setAppId is required for drive.file so
        // both the API key and the project number must be configured.
        DrivePublishingOptions options = _options.Value;
        bool pickerEnabled = options.IsGoogleConfigured
            && !string.IsNullOrWhiteSpace(options.GooglePickerApiKey)
            && !string.IsNullOrWhiteSpace(options.GooglePickerAppId);
        return new DriveStatusResponse(
            detail.SaveId,
            detail.Name,
            detail.CurrentSeason,
            detail.Phase,
            options.IsGoogleConfigured,
            connection is not null && !string.IsNullOrWhiteSpace(connection.RefreshToken),
            connection?.Scopes,
            connection?.ConnectedUtc,
            new DriveReportSlotDto(
                dossierId,
                dossierName,
                dossierFingerprint,
                dossierModified,
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
            destination?.SeasonsFolderId,
            destination?.LastPublishedUtc,
            destination?.LastSeasonNumber,
            destination?.LastPhase,
            destination?.LastGlobalStage,
            destination?.LastError,
            string.IsNullOrWhiteSpace(options.GoogleClientId) ? null : options.GoogleClientId,
            string.IsNullOrWhiteSpace(options.GooglePickerApiKey) ? null : options.GooglePickerApiKey,
            string.IsNullOrWhiteSpace(options.GooglePickerAppId) ? null : options.GooglePickerAppId,
            pickerEnabled);
    }
}
