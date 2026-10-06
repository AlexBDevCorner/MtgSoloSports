namespace MtgSoloSports.Features.Leagues.GetSeason1Leagues;

/// <summary>
/// One feeder league roster in inaugural draw order. Division is explicit so
/// presentation never parses league names: 1 = Feeder 1, 2 = Feeder 2,
/// 3 = Feeder 3. <see cref="LeagueLevel"/> carries the canonical tier name.
/// </summary>
public sealed record Season1LeagueRoster(
    string LeagueName,
    string SportingColor,
    int LeagueId,
    int FeederDivision,
    string LeagueLevel,
    IReadOnlyList<Season1RosterAthlete> Athletes);
