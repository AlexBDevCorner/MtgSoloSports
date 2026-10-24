namespace MtgSoloSports.Features.Leagues.Profiles;

/// <summary>
/// The current (not yet complete) season for a league, shown separately and
/// never counted in historical awards, records or all-time totals.
/// </summary>
public sealed record CurrentSeasonInfo(
    int SeasonNumber,
    int LeagueId,
    bool IsComplete,
    bool HasFinalTable);
