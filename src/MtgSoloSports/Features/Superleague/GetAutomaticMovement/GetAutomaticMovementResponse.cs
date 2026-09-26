using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;

namespace MtgSoloSports.Features.Superleague.GetAutomaticMovement;

/// <summary>
/// Immutable read model for the latest (or requested) automatic movement.
/// Built only from persisted next-season rows plus movement history so replay
/// never resimulates. Suitable for dashboard/story use: safe incumbents,
/// automatic promotions/relegations and qualifier candidates with source
/// rank provenance and next-league assignment.
/// </summary>
public sealed record GetAutomaticMovementResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    int SuperleagueLeagueId,
    string SuperleagueLeagueName,
    IReadOnlyList<AutomaticMovementMember> Safe,
    IReadOnlyList<AutomaticMovementMember> Promoted,
    IReadOnlyList<AutomaticMovementMember> Relegated,
    IReadOnlyList<AutomaticMovementMember> QualifierIncumbents,
    IReadOnlyList<AutomaticMovementMember> QualifierChallengers,
    int PoolCount,
    int MovementCount);
