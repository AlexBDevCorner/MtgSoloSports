namespace MtgSoloSports.SimulationKernel.Leagues;

/// <summary>
/// Competitive tier in the four-level pyramid: Superleague → Feeder 1 → Feeder 2 → Feeder 3.
/// Every active league holds 32 athletes. The pool is not a competitive division.
/// This is domain data, never presentation text parsed from league names.
/// </summary>
public enum LeagueLevel
{
    Superleague = 0,
    Feeder1 = 1,
    Feeder2 = 2,
    Feeder3 = 3,
}
