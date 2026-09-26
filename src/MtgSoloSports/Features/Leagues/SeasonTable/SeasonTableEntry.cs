namespace MtgSoloSports.Features.Leagues.SeasonTable;

/// <summary>
/// One athlete's final season table row for presentation.
/// All sporting values are fixed-point thousandths integers.
/// </summary>
public sealed record SeasonTableEntry(
    int AthleteId,
    string Name,
    int SeasonRank,
    int TotalChampionshipPointsThousandths,
    int TotalStageScoreThousandths,
    int TotalBaseScoreThousandths,
    int StageWins,
    int RoundWins,
    bool IsChampion);
