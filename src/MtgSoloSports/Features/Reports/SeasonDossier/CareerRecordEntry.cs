namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal sealed record CareerRecordEntry(
    string RecordKey,
    string Label,
    int Value,
    string ValueDisplay,
    IReadOnlyList<CareerRecordHolder> Holders);
