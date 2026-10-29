namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed record PutDriveDestinationRequest(
    string? MtshistoryFolderId,
    string? MtshistoryFolderName,
    string? SeasonsFolderId,
    string? DossierFileId,
    string? DossierFileName,
    string? AlmanacFileId,
    string? AlmanacFileName,
    bool ConfirmCrossSave);
