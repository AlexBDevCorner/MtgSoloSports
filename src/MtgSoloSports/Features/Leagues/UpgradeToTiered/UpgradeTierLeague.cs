namespace MtgSoloSports.Features.Leagues.UpgradeToTiered;

/// <summary>
/// One created F2/F3 league row for the target season.
/// </summary>
public sealed record UpgradeTierLeague(
    int LeagueId,
    string LeagueName,
    string SportingColor,
    int FeederDivision,
    string LeagueLevel,
    int AthleteCount);
