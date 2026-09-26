namespace MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;

/// <summary>
/// Immutable presentation DTO for normal automatic Superleague movement.
/// Built only from persisted next-season rows so replay never resimulates.
/// The next Superleague provisionally holds 16 safe incumbents plus 8
/// automatically promoted feeder champions plus 8 qualifier incumbents still
/// awaiting the qualifier (MSS-016); feeder pools are never filled here.
/// Relegated athletes already sit in their returning-color feeders pending
/// rebalancing (MSS-017). Counts: safe 16, promoted 8, relegated 8,
/// qualifier incumbents 8, qualifier challengers 24, movements 48.
/// </summary>
public sealed record ResolveAutomaticMovementResponse(
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
