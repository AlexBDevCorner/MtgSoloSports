namespace MtgSoloSports.Features.Simulation.CompleteStage;

/// <summary>
/// One athlete's immutable standing within a completed stage.
/// All sporting values are fixed-point thousandths integers.
/// </summary>
public sealed record CompleteStageStanding(
    int AthleteId,
    string Name,
    int StageRank,
    int StageScoreThousandths,
    int BaseScoreThousandths,
    int ChampionshipPointsThousandths,
    int RoundWins,
    int EarnedBonusThousandths);
