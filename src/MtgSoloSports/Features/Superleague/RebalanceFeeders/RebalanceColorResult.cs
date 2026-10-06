namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Per-league rebalancing outcome for one feeder division (MSS-059 tier cascade).
/// One entry per feeder league (24 total: F1/F2/F3 per sporting color).
/// <c>StartingCount</c> is the source division size (always 32). After all
/// automatic movement and qualifier outcomes the league holds
/// <c>ProvisionalCount</c>; the structural cascade (F1↔F2/F2↔F3 up/down plus
/// F3↔pool draws/displacements) then restores <c>FinalCount</c> 32.
/// <c>DepartedCount</c>/<c>ReturnedCount</c> are Superleague transfers (F1
/// only, zero for F2/F3). <c>RebalancedUpIn/Out</c> and
/// <c>RebalancedDownIn/Out</c> count structural F1↔F2/F2↔F3 moves;
/// <c>DisplacedCount</c>/<c>DrawnCount</c> count F3↔pool boundary moves only.
/// <c>FeederDivision</c> carries the persisted division (1/2/3) so history/UI
/// can group by color and tier without resimulation. Built only from persisted
/// rows so replay never resimulates.
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
    int FinalCount)
{
    public int FeederDivision { get; init; }

    public int RebalancedUpIn { get; init; }

    public int RebalancedUpOut { get; init; }

    public int RebalancedDownIn { get; init; }

    public int RebalancedDownOut { get; init; }

    public int F2ProvisionalCount { get; init; }

    public int F3ProvisionalCount { get; init; }
}
