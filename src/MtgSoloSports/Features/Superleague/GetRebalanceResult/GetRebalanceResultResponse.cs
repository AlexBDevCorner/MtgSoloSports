using MtgSoloSports.Features.Superleague.RebalanceFeeders;

namespace MtgSoloSports.Features.Superleague.GetRebalanceResult;

/// <summary>
/// Immutable read model for feeder rebalancing.
/// Built only from persisted next-season rows plus movement history so replay
/// never resimulates. Suitable for dashboard/story use: per-color provisional
/// and final counts with pool-transfer provenance.
/// </summary>
public sealed record GetRebalanceResultResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    IReadOnlyList<RebalanceColorResult> Colors,
    IReadOnlyList<RebalanceMovementMember> Draws,
    IReadOnlyList<RebalanceMovementMember> Displaced,
    int TotalDrawn,
    int TotalDisplaced,
    int PoolCount,
    int MovementCount);
