namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-091: one library entry per authoritative Markdown export.
/// </summary>
internal sealed record DriveLibraryItem(
    string Category,
    string Report,
    string DefaultFileName,
    string ExpectedLocation,
    int? SeasonNumber,
    string? Cup,
    string? TeamKey);
