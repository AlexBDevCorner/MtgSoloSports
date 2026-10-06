namespace MtgSoloSports.Features.Leagues.UpgradeToTiered;

/// <summary>
/// One athlete seeded from the pool into F2/F3.
/// </summary>
public sealed record UpgradeTierSeed(
    int SaveAthleteId,
    string Name,
    string SportingColor,
    int ToLeagueId,
    string ToLeagueName,
    int FeederDivision,
    string? ImageUrl);
