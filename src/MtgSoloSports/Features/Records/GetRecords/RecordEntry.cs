namespace MtgSoloSports.Features.Records.GetRecords;

/// <summary>
/// One career record with its current value and joint holders.
/// Values are fixed-point thousandths for bonus records, plain counts otherwise.
/// </summary>
public sealed record RecordEntry(
    string RecordKey,
    string Label,
    int Value,
    string ValueDisplay,
    bool IsBonus,
    bool IsVacant,
    IReadOnlyList<RecordHolderEntry> Holders);
