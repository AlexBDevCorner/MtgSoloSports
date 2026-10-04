namespace MtgSoloSports.Features.Athletes.GetRecordHoldings;

/// <summary>
/// One career record held by the requested athlete. Values are fixed-point
/// thousandths for bonus records, plain counts otherwise. Labels and value
/// display use the same deterministic formatting as the Records page so the
/// profile shows exactly the authoritative career-record holdings.
/// </summary>
public sealed record RecordHoldingEntry(
    string RecordKey,
    string Label,
    int Value,
    string ValueDisplay,
    bool IsBonus);
