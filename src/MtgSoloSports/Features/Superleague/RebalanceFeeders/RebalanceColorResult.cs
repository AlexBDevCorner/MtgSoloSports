namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Per-color rebalancing outcome for one feeder league.
/// <c>StartingCount</c> is the source feeder size (always 32). After
/// Superleague departures/returns the league holds <c>ProvisionalCount</c>
/// (32 - departed + returned); pool draws/displacements then restore
/// <c>FinalCount</c> 32. Built only from persisted rows so replay never
/// resimulates.
/// </summary>
public sealed record RebalanceColorResult(
    int LeagueId,
    string LeagueName,
    string SportingColor,
    int StartingCount,
    int DepartedCount,
    int ReturnedCount,
    int ProvisionalCount,
    int DisplacedCount,
    int DrawnCount,
    int FinalCount);
