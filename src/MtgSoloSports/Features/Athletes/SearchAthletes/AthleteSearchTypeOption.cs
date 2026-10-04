namespace MtgSoloSports.Features.Athletes.SearchAthletes;

/// <summary>
/// Creature-type filter option with the athlete count in the current save.
/// </summary>
public sealed record AthleteSearchTypeOption(
    string Value,
    int Count);
