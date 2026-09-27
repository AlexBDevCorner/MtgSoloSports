namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Immutable presentation DTO for feeder rebalancing.
/// Built only from persisted next-season rows so replay never resimulates.
/// Each feeder holds exactly 32 color-matched athletes; pool draws use the
/// versioned simulation RNG committed in the same transaction.
/// </summary>
public sealed record RebalanceFeedersResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    IReadOnlyList<RebalanceColorResult> Colors,
    IReadOnlyList<RebalanceMovementMember> Draws,
    IReadOnlyList<RebalanceMovementMember> Displaced,
    int TotalDrawn,
    int TotalDisplaced,
    int PoolCount,
    int MovementCount,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream);
