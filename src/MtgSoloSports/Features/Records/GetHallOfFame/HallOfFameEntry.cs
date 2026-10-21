namespace MtgSoloSports.Features.Records.GetHallOfFame;

/// <summary>
/// One Hall of Fame leader row. <c>CareerPoints</c> is the MSS-083 weighted
/// performance score for the selected season range; titles are period-scoped
/// supporting statistics. Legacy stage/round/tenure/bonus fields remain
/// all-time career totals for compatibility and are not part of the score.
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
    int LongestStageWinStreak,
    int CareerPoints,
    int SuperleaguePoints,
    int Feeder1Points,
    int Feeder2Points,
    int Feeder3Points,
    int ColorCupIndividualPoints,
    int ColorCupTeamPoints,
    int TypeCupTeamPoints,
    int MajorWins,
    int ScoredEvents);
