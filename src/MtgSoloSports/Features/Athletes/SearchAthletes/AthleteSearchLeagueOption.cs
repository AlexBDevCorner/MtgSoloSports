namespace MtgSoloSports.Features.Athletes.SearchAthletes;

/// <summary>
/// Current league/pool filter option with the athlete count in the current save.
/// LeagueLevel carries the tier identity ("Superleague", "Feeder1", "Feeder2",
/// "Feeder3") and FeederDivision the persisted division (0 for historical v1
/// single-feeder leagues), resolved from league rows without parsing league
/// names (MSS-060). Both are null for the common pool.
/// </summary>
public sealed record AthleteSearchLeagueOption(
    string Name,
    int? Kind,
    bool IsPool,
    int Count,
    string? LeagueLevel = null,
    int? FeederDivision = null);
