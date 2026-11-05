namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-091: per-category sync result plus the per-file outcomes (same shape
/// as single-file publishing). <c>Truncated</c> means <c>MaxFiles</c> cut
/// the inventory short: resume with <c>Skip = NextSkip</c> for the
/// remainder; already-persisted files report unchanged on retry.
/// <c>TotalItems</c> is the full filtered inventory size; <c>Files</c> holds
/// only the current page.
/// </summary>
public sealed record DriveLibrarySyncResponse(
    Guid SaveId,
    bool DryRun,
    bool Truncated,
    int TotalItems,
    int? NextSkip,
    DateTimeOffset SyncedUtc,
    IReadOnlyList<DriveLibraryCategoryResult> Categories,
    IReadOnlyList<DriveFileOutcome> Files);
