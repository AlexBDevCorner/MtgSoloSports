namespace MtgSoloSports.Features.Leagues.GetSeason1Leagues;

/// <summary>
/// One feeder league roster in inaugural draw order.
/// </summary>
public sealed record Season1LeagueRoster(
    string LeagueName,
    string SportingColor,
    int LeagueId,
    IReadOnlyList<Season1RosterAthlete> Athletes);
