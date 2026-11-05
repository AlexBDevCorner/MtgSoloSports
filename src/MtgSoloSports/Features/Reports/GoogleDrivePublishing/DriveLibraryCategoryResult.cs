namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

public sealed record DriveLibraryCategoryResult(
    string Category,
    int Total,
    int Created,
    int Updated,
    int Unchanged,
    int Failed);
