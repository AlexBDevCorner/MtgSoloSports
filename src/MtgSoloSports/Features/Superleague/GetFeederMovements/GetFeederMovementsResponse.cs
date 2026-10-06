namespace MtgSoloSports.Features.Superleague.GetFeederMovements;

/// <summary>
/// Immutable read model for the feeder automatic movement of one season
/// transition (MSS-060). Complements the Superleague-scoped automatic
/// movement: every competitive feeder move (automatic promotion/relegation
/// and qualifier incumbents/challengers) grouped by boundary via
/// <see cref="FeederMovementMember.Boundary"/> and sporting color. Built only
/// from persisted movement rows plus next-season memberships so replay never
/// resimulates. Qualifier outcomes (who won) come from the qualifier-list API;
/// structural cascade moves come from the rebalance result.
/// </summary>
public sealed record GetFeederMovementsResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    IReadOnlyList<FeederMovementMember> Movements,
    int MovementCount);
