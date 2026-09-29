namespace MtgSoloSports.Features.History.GetSeasonPlacements;

/// <summary>
/// One compact persisted stage cell: the athlete's actual finishing place in
/// one completed stage plus the bonus/points earned there. Only completed
/// stages appear; future or in-progress stages are absent, never zero.
/// <c>EarnedBonusThousandths</c> becomes active only from the next stage.
/// </summary>
public sealed record SeasonPlacementCell(
    int AthleteId,
    int StageNumber,
    int StageRank,
    int EarnedBonusThousandths,
    int ChampionshipPointsThousandths,
    int StageScoreThousandths,
    int RoundWins);
