namespace MtgSoloSports.Features.Athletes.SearchAthletes;

/// <summary>
/// Colour filter option with the athlete count in the current save.
/// </summary>
public sealed record AthleteSearchColourOption(
    int Value,
    string Name,
    int Count);
