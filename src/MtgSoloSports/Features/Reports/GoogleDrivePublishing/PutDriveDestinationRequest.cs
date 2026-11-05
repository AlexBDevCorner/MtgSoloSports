namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed record PutDriveDestinationRequest(
    string? MtshistoryFolderId,
    string? MtshistoryFolderName,
    string? SeasonsFolderId,
    string? DossierFileId,
    string? DossierFileName,
    string? AlmanacFileId,
    string? AlmanacFileName,
    bool ConfirmCrossSave,
    string? ColorCupFolderId = null,
    string? ColorCupsFolderId = null,
    string? ColorTeamsFolderId = null,
    string? TypeCupFolderId = null,
    string? TypeCupsFolderId = null,
    string? TypeTeamsFolderId = null,
    IReadOnlyDictionary<string, DriveLibraryAdoption>? DossierAdoptions = null,
    IReadOnlyDictionary<string, DriveLibraryAdoption>? EditionAdoptions = null,
    IReadOnlyDictionary<string, DriveLibraryAdoption>? TeamAdoptions = null);
