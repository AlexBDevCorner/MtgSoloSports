namespace MtgSoloSports.SimulationKernel.Seasons;

/// <summary>
/// One athlete's final standing within a completed (or provisional) season.
/// Totals accumulate stage championship points across completed stages.
/// <see cref="IsChampion"/> is true only for season rank 1.
/// </summary>
public sealed record SeasonRankedAthlete(
    int AthleteId,
    string Name,
    int SeasonRank,
    int TotalChampionshipPointsThousandths,
    int TotalStageScoreThousandths,
    int TotalBaseScoreThousandths,
    int StageWins,
    int RoundWins,
    IReadOnlyList<int> StagePlaceCounts,
    IReadOnlyList<int> RoundPlaceCounts,
    bool IsChampion);
