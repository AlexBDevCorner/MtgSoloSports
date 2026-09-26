namespace MtgSoloSports.SimulationKernel.TieBreaking;

/// <summary>
/// Deterministic portion of a standings key: stage-place counts from best
/// downward, then round-place counts from best downward, then raw totals
/// (higher is better). Vectors use index 0 for first places.
/// </summary>
public sealed record DeterministicTieBreakKey(
    IReadOnlyList<int> StagePlaceCounts,
    IReadOnlyList<int> RoundPlaceCounts,
    long RawTotalThousandths);
