namespace MtgSoloSports.SimulationKernel.Seasons;

/// <summary>
/// One athlete's result within one completed stage, as input to season accumulation.
/// Pure kernel input: no persistence, HTTP or EF dependencies.
/// All sporting values are fixed-point thousandths integers.
/// <see cref="RoundPlaceCounts"/> has one entry per finishing position
/// (index 0 counts round 1st places); entries sum to the stage round count.
/// </summary>
public sealed record SeasonStageEntry(
    int AthleteId,
    string Name,
    int StageRank,
    int ChampionshipPointsThousandths,
    int StageScoreThousandths,
    int BaseScoreThousandths,
    IReadOnlyList<int> RoundPlaceCounts);
