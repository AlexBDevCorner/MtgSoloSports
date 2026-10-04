namespace MtgSoloSports.Features.Athletes.GetRecordHoldings;

/// <summary>
/// Immutable athlete record-holdings response: only the career records the
/// requested athlete currently holds (ties included). Vacant records are never
/// returned. Scoring records are intentionally excluded; the athlete page does
/// not display them.
/// </summary>
public sealed record GetAthleteRecordHoldingsResponse(
    Guid SaveId,
    int AthleteId,
    IReadOnlyList<RecordHoldingEntry> Holdings);
