using MtgSoloSports.Features.Superleague.RebalanceFeeders;

namespace MtgSoloSports.Features.Superleague.GetRebalanceResult;

/// <summary>
/// Immutable read model for tier-cascade feeder rebalancing (MSS-059).
/// Built only from persisted next-season rows plus movement history so replay
/// never resimulates. Per-league (24 F1/F2/F3) provisional and final counts
/// with structural up/down plus F3↔pool provenance grouped by color and tier.
/// </summary>
public sealed record GetRebalanceResultResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    IReadOnlyList<RebalanceColorResult> Colors,
    IReadOnlyList<RebalanceMovementMember> Draws,
    IReadOnlyList<RebalanceMovementMember> Displaced,
    IReadOnlyList<RebalanceMovementMember> Departed,
    IReadOnlyList<RebalanceMovementMember> Returned,
    int TotalDrawn,
    int TotalDisplaced,
    int TotalDeparted,
    int TotalReturned,
    int PoolCount,
    int MovementCount)
{
    public IReadOnlyList<RebalanceMovementMember> RebalancedUp { get; init; } = [];

    public IReadOnlyList<RebalanceMovementMember> RebalancedDown { get; init; } = [];

    public int TotalRebalancedUp { get; init; }

    public int TotalRebalancedDown { get; init; }
}
