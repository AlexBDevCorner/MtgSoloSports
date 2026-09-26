using MtgSoloSports.SimulationKernel.FixedPoint;

namespace MtgSoloSports.SimulationKernel.Scoring;

/// <summary>
/// Aggregated bonus earned during one season.
/// Used for season-boundary calculations where intra-season activation is settled.
/// </summary>
public sealed record SeasonBonus(int Season, Bonus Total);
