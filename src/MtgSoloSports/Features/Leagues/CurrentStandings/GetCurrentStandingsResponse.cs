namespace MtgSoloSports.Features.Leagues.CurrentStandings;

/// <summary>
/// Immutable presentation DTO for a league's current season standings.
/// Accumulated live from persisted completed-stage standings (never
/// resimulated); when the season is complete it reads the persisted final
/// table so provisional and final views agree. <see cref="IsFinal"/> is true
/// only for a completed season; provisional leaders are not champions.
/// </summary>
public sealed record GetCurrentStandingsResponse(
    Guid SaveId,
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    int CompletedStages,
    int GlobalStage,
    bool IsSeasonComplete,
    bool IsFinal,
    string SeasonChecksum,
    IReadOnlyList<CurrentStandingEntry> Standings);
