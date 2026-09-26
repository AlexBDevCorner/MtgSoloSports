namespace MtgSoloSports.Features.Leagues.GetSeason1Leagues;

/// <summary>
/// One athlete roster entry in inaugural draw order.
/// </summary>
public sealed record Season1RosterAthlete(
    string Name,
    int DrawIndex);
