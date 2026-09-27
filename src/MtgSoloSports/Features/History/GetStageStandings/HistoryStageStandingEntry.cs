namespace MtgSoloSports.Features.History.GetStageStandings;

/// <summary>
/// Normalized stage standing entry for history views. Built only from the
/// persisted <c>StageStanding</c> summary rows; never decompresses round payloads.
/// </summary>
public sealed record HistoryStageStandingEntry(
    int AthleteId,
    string Name,
    int StageRank,
    int StageScoreThousandths,
    int BaseScoreThousandths,
    int ChampionshipPointsThousandths,
    int RoundWins,
    int EarnedBonusThousandths);
