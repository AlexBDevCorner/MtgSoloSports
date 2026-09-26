using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.SimulationKernel.Scoring;

/// <summary>
/// A single earned bonus contribution from one stage.
/// Earned bonus becomes active only from the next stage; Stage 32 bonus
/// therefore first becomes usable in the next season.
/// </summary>
public sealed record BonusContribution(int EarnedSeason, int EarnedStage, Bonus Earned);
