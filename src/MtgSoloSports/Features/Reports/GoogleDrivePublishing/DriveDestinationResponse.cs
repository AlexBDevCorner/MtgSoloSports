namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed record DriveDestinationResponse(
    Guid SaveId,
    string SaveName,
    string? MtshistoryFolderId,
    string? MtshistoryFolderName,
    string? SeasonsFolderId,
    string? DossierFileId,
    string? DossierFileName,
    string? AlmanacFileId,
    string? AlmanacFileName,
    string? DossierFingerprint,
    string? AlmanacFingerprint,
    DateTimeOffset? LastPublishedUtc,
    int? LastSeasonNumber,
    string? LastPhase,
    int? LastGlobalStage,
    string? LastError,
    bool AutoPublishEnabled,
    string AutoPublishStatus,
    int? AutoRequestedSeason,
    int? AutoRequestedGlobalStage,
    string? AutoRequestedPhase);
