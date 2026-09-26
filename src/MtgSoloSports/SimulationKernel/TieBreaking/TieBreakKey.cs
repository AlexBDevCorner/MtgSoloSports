namespace MtgSoloSports.SimulationKernel.TieBreaking;

/// <summary>
/// Full standings key including the supplied seeded final draw.
/// The draw (smaller wins) is consulted only when every deterministic field ties.
/// Callers must supply unique draws within each deterministically tied group.
/// </summary>
public sealed record TieBreakKey(
    IReadOnlyList<int> StagePlaceCounts,
    IReadOnlyList<int> RoundPlaceCounts,
    long RawTotalThousandths,
    uint FinalDraw);
