namespace MtgSoloSports.Features.Superleague.GetInauguralRoster;

/// <summary>
/// Immutable read model for the inaugural Season 2 Superleague roster.
/// Built only from persisted Season 2 memberships plus movement history so
/// replay never resimulates. Members are ordered by source league then source
/// rank for a stable presentation.
/// </summary>
public sealed record GetInauguralRosterResponse(
    Guid SaveId,
    int SeasonNumber,
    int SuperleagueLeagueId,
    string SuperleagueLeagueName,
    IReadOnlyList<InauguralRosterMember> Members,
    int MovementCount);
