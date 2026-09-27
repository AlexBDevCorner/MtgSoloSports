namespace MtgSoloSports.Features.Records.GetRecords;

/// <summary>
/// Immutable records response: current holders per record plus recent
/// record-break history. Computed live from normalized projections without
/// round payloads; history comes from persisted <c>new_record</c> stories.
/// </summary>
public sealed record GetRecordsResponse(
    Guid SaveId,
    IReadOnlyList<RecordEntry> Records,
    IReadOnlyList<RecordHistoryEntry> RecentHistory);
