namespace MtgSoloSports.SimulationKernel.Stages;

/// <summary>
/// Accumulated stage totals for one athlete across all rounds of a stage.
/// <see cref="RoundPlaceCounts"/> has one entry per finishing position
/// (index 0 counts round 1st places); its entries sum to the stage round count.
/// </summary>
public sealed record StageAthleteTotals(
    int AthleteId,
    string Name,
    int StageScoreThousandths,
    int BaseScoreThousandths,
    IReadOnlyList<int> RoundPlaceCounts)
{
    public int RoundWins => RoundPlaceCounts.Count > 0 ? RoundPlaceCounts[0] : 0;
}
