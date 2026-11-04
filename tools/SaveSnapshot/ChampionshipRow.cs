namespace MtgSoloSports.SaveSnapshot;

public sealed record ChampionshipRow(
    int AthleteId,
    string Name,
    int Rank,
    int TotalChampionshipPointsThousandths,
    string TotalChampionshipPointsDisplay,
    int TotalStageScoreThousandths,
    string TotalStageScoreDisplay,
    int StageWins,
    int RoundWins,
    int GapToLeaderThousandths,
    string GapToLeaderDisplay,
    bool DeterministicTie,
    int TieGroupSize,
    int? RankBefore,
    int? RankChange,
    int? PointsBefore,
    int? PointsAwarded,
    int? GapToLeaderBeforeThousandths,
    int? GapChangeThousandths);
