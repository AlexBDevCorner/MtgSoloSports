using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.SimulationKernel.Scoring;

/// <summary>
/// Immutable result of simulating one league round.
/// Placements are in finishing order (position 1 first).
/// </summary>
public sealed record RoundSimulationResult(
    IReadOnlyList<RoundPlacement> Placements,
    Pcg32State RngAfter,
    string Checksum);
