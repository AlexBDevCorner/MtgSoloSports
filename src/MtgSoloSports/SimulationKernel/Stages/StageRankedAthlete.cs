namespace MtgSoloSports.SimulationKernel.Stages;

/// <summary>
/// One athlete's final standing within a completed stage.
/// <see cref="ChampionshipPointsThousandths"/> uses the 32-position scoring
/// table with no bonus multiplier. <see cref="EarnedBonusThousandths"/> is the
/// round-plus-stage bonus earned during this stage; it stays pending and
/// becomes active only from the next stage boundary.
/// </summary>
public sealed record StageRankedAthlete(
    int AthleteId,
    string Name,
    int StageRank,
    int StageScoreThousandths,
    int BaseScoreThousandths,
    int ChampionshipPointsThousandths,
    int RoundWins,
    IReadOnlyList<int> RoundPlaceCounts,
    int EarnedBonusThousandths);
