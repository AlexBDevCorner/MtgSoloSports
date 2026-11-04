namespace MtgSoloSports.SaveSnapshot;

public sealed record StageTableRow(
    int AthleteId,
    string Name,
    int StageRank,
    int StageScoreThousandths,
    string StageScoreDisplay,
    int BaseScoreThousandths,
    string BaseScoreDisplay,
    int ChampionshipPointsThousandths,
    string ChampionshipPointsDisplay,
    int RoundWins,
    int EarnedBonusThousandths,
    string EarnedBonusDisplay,
    IReadOnlyList<int> RoundPlaceCounts,
    int? RankBefore,
    int RankAfter,
    int? RankChange,
    int PointsBefore,
    int PointsAfter,
    int? GapToLeaderBeforeThousandths,
    int GapToLeaderAfterThousandths,
    string EvidenceRef);
