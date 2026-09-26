namespace MtgSoloSports.SimulationKernel.Seasons;

/// <summary>
/// Accumulated season totals for one athlete across completed stages.
/// <see cref="StagePlaceCounts"/> has one entry per stage position
/// (index 0 counts stage wins); entries sum to the completed stage count.
/// <see cref="RoundPlaceCounts"/> has one entry per round position
/// (index 0 counts round wins); entries sum to completed stages times rounds per stage.
/// </summary>
public sealed record SeasonAthleteTotals(
    int AthleteId,
    string Name,
    int TotalChampionshipPointsThousandths,
    int TotalStageScoreThousandths,
    int TotalBaseScoreThousandths,
    IReadOnlyList<int> StagePlaceCounts,
    IReadOnlyList<int> RoundPlaceCounts)
{
    public int StageWins => StagePlaceCounts.Count > 0 ? StagePlaceCounts[0] : 0;

    public int RoundWins => RoundPlaceCounts.Count > 0 ? RoundPlaceCounts[0] : 0;
}
