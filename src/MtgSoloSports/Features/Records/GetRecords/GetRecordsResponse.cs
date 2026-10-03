namespace MtgSoloSports.Features.Records.GetRecords;

/// <summary>
/// Immutable records response: current career holders per record, scoring
/// records derived from historical persisted round/stage/event results, plus
/// recent record-break history. Career values are computed live from
/// normalized projections without round payloads; scoring records decode the
/// immutable compact round payloads plus normalized stage/season totals and
/// never resimulate. History comes from persisted <c>new_record</c> stories.
/// </summary>
public sealed record GetRecordsResponse(
    Guid SaveId,
    IReadOnlyList<RecordEntry> Records,
    IReadOnlyList<RecordHistoryEntry> RecentHistory,
    IReadOnlyList<ScoringRecordEntry> ScoringRecords);
