namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// MSS-091: per-category sync result plus the per-file outcomes (same shape
/// as single-file publishing). <c>Truncated</c> means <c>MaxFiles</c> cut
/// the inventory short: re-run to resume the remainder; already-persisted
/// files report unchanged on retry.
/// </summary>
public sealed record DriveLibrarySyncResponse(
    Guid SaveId,
    bool DryRun,
    bool Truncated,
    int TotalItems,
    DateTimeOffset SyncedUtc,
    IReadOnlyList<DriveLibraryCategoryResult> Categories,
    IReadOnlyList<DriveFileOutcome> Files);
