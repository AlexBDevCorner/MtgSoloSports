namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-091: one library entry per authoritative Markdown export.
/// MSS-093 adds athlete entries: stable save-scoped athlete id plus stable
/// sporting color for folder routing. The filename keeps the stable id plus a
/// decorative slug so renames keep the same mapping.
/// </summary>
internal sealed record DriveLibraryItem(
    string Category,
    string Report,
    string DefaultFileName,
    string ExpectedLocation,
    int? SeasonNumber,
    string? Cup,
    string? TeamKey,
    int? AthleteId = null,
    string? AthleteColor = null);
