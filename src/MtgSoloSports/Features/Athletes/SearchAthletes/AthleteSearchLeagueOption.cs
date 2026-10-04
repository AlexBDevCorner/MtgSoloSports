namespace MtgSoloSports.Features.Athletes.SearchAthletes;

/// <summary>
/// Current league/pool filter option with the athlete count in the current save.
/// </summary>
public sealed record AthleteSearchLeagueOption(
    string Name,
    int? Kind,
    bool IsPool,
    int Count);
