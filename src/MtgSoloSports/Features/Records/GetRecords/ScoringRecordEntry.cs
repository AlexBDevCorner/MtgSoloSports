namespace MtgSoloSports.Features.Records.GetRecords;

/// <summary>
/// One scoring record with its current value and joint holders.
/// Values are fixed-point thousandths points. Category groups related
/// records (League, Individual Cups, Team Cups); Scope identifies the
/// league or event type so incompatible formats are never compared.
/// </summary>
public sealed record ScoringRecordEntry(
    string RecordKey,
    string Label,
    string Category,
    string Scope,
    int Value,
    string ValueDisplay,
    bool IsVacant,
    IReadOnlyList<ScoringRecordHolderEntry> Holders);
