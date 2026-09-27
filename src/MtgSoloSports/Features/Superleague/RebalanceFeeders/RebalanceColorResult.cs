namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Per-color rebalancing outcome for one feeder league.
/// </summary>
public sealed record RebalanceColorResult(
    int LeagueId,
    string LeagueName,
    string SportingColor,
    int ProvisionalCount,
    int DisplacedCount,
    int DrawnCount,
    int FinalCount);
