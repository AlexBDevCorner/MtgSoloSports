namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed record PublishDriveReportsResponse(
    Guid SaveId,
    int SeasonNumber,
    string Phase,
    int? GlobalStage,
    DateTimeOffset PublishedUtc,
    IReadOnlyList<DriveFileOutcome> Files);
