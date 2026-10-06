namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Immutable presentation DTO for tier-cascade feeder rebalancing (MSS-059).
/// Built only from persisted next-season rows so replay never resimulates.
/// Every F1/F2/F3 league holds exactly 32 color-matched athletes; the pool
/// only ever connects directly to F3. <c>RebalancedUp</c> holds structural
/// F2→F1/F3→F2 moves, <c>RebalancedDown</c> holds F1→F2/F2→F3 moves,
/// <c>Draws</c> holds Pool→F3 draws and <c>Displaced</c> holds F3→Pool moves;
/// <c>Departed</c>/<c>Returned</c> remain derived Superleague transfers.
/// Pool draws use the versioned simulation RNG committed atomically.
/// </summary>
public sealed record RebalanceFeedersResponse(
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
    int MovementCount,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream)
{
    public IReadOnlyList<RebalanceMovementMember> RebalancedUp { get; init; } = [];

    public IReadOnlyList<RebalanceMovementMember> RebalancedDown { get; init; } = [];

    public int TotalRebalancedUp { get; init; }

    public int TotalRebalancedDown { get; init; }
}
