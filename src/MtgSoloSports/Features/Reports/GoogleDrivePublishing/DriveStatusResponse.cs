namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed record DriveStatusResponse(
    Guid SaveId,
    string SaveName,
    int CurrentSeason,
    string Phase,
    bool Configured,
    bool Connected,
    string? Scopes,
    DateTimeOffset? ConnectedUtc,
    DriveReportSlotDto Dossier,
    DriveReportSlotDto Almanac,
    string? MtshistoryFolderId,
    string? MtshistoryFolderName,
    DateTimeOffset? LastPublishedUtc,
    int? LastSeasonNumber,
    string? LastPhase,
    int? LastGlobalStage,
    string? LastError,
    string? PickerClientId,
    string? PickerApiKey,
    string? PickerAppId,
    bool PickerEnabled);
