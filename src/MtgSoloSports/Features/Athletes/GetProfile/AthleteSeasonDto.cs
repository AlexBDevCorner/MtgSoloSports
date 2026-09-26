namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One season of an athlete's history for presentation.
/// </summary>
public sealed record AthleteSeasonDto(
    int SeasonNumber,
    int SeasonId,
    bool WasActive,
    int? LeagueId,
    string? LeagueName,
    int? LeagueKind,
    int RoundWins,
    int StageWins,
    int StageSeconds,
    int StageThirds,
    int? SeasonRank,
    bool IsChampion,
    int EarnedBonusThousandths,
    int TotalChampionshipPointsThousandths,
    int TotalStageScoreThousandths,
    int TotalBaseScoreThousandths);
