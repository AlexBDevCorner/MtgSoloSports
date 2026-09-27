namespace MtgSoloSports.Features.Records.GetHallOfFame;

/// <summary>
/// One Hall of Fame leader row with honour and record-relevant career totals.
/// All sporting values are fixed-point thousandths integers where applicable.
/// </summary>
public sealed record HallOfFameEntry(
    int Rank,
    int AthleteId,
    string AthleteName,
    int SportingColor,
    string SportingColorName,
    int FeederTitles,
    int SuperleagueTitles,
    int TotalTitles,
    int StageWins,
    int RoundWins,
    int SuperleagueAppearances,
    int TotalAppearances,
    int LongestSuperleagueTenure,
    int Promotions,
    int Relegations,
    int CurrentEffectiveBonusThousandths,
    int LongestTitleStreak,
    int LongestStageWinStreak);
