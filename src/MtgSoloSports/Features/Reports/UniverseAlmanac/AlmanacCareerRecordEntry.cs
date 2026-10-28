namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal sealed record AlmanacCareerRecordEntry(
    string RecordKey,
    string Label,
    int Value,
    string ValueDisplay,
    IReadOnlyList<AlmanacRecordHolder> Holders);
