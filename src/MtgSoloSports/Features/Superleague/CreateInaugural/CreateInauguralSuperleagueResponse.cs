namespace MtgSoloSports.Features.Superleague.CreateInaugural;

/// <summary>
/// Immutable presentation DTO for the Season 1 inaugural Superleague
/// transition. Built only from persisted Season 2 rows so replay never
/// resimulates. Feeder leagues hold 28 retained athletes each pending the
/// rebalancing flow; pool fills never happen in this slice.
/// </summary>
public sealed record CreateInauguralSuperleagueResponse(
    Guid SaveId,
    int SeasonOneNumber,
    int SeasonTwoNumber,
    int SuperleagueLeagueId,
    string SuperleagueLeagueName,
    IReadOnlyList<InauguralSuperleagueMember> Members,
    IReadOnlyList<InauguralFeederRetention> FeederRetention,
    int PoolCount,
    int MovementCount);
