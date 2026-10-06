namespace MtgSoloSports.SimulationKernel.Leagues;

/// <summary>
/// Adjacent-tier postseason boundary that owns one qualifier event.
/// Superleague covers Superleague ↔ Feeder 1 (no sporting color, mixed field).
/// Feeder1Feeder2 covers F1 ↔ F2 per sporting color; Feeder2Feeder3 covers
/// F2 ↔ F3 per sporting color. Every normal movement is between adjacent
/// levels only; there is no division skipping. Pure: no HTTP, EF Core,
/// filesystem, clock or network dependencies.
/// </summary>
public enum QualifierBoundary
{
    Superleague = 0,
    Feeder1Feeder2 = 1,
    Feeder2Feeder3 = 2,
}
