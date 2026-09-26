namespace MtgSoloSports.Features.Leagues.CurrentStandings;

/// <summary>
/// One athlete's season standing row for presentation.
/// All sporting values are fixed-point thousandths integers.
/// </summary>
public sealed record CurrentStandingEntry(
    int AthleteId,
    string Name,
    int SeasonRank,
    int TotalChampionshipPointsThousandths,
    int TotalStageScoreThousandths,
    int TotalBaseScoreThousandths,
    int StageWins,
    int RoundWins,
    bool IsChampion);
