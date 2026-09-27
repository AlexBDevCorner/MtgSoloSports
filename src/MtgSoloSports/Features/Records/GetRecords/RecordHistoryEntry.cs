namespace MtgSoloSports.Features.Records.GetRecords;

/// <summary>
/// One persisted record-break story for history views.
/// </summary>
public sealed record RecordHistoryEntry(
    int AthleteId,
    string AthleteName,
    string RecordKey,
    int Value,
    int PriorValue,
    int SeasonNumber,
    string Text);
