namespace MtgSoloSports.Features.History.GetSeasonTable;

/// <summary>
/// Normalized season-table entry for history views. Built only from the
/// persisted <c>SeasonStanding</c> summary rows; never decompresses round payloads.
/// </summary>
public sealed record HistorySeasonTableEntry(
    int AthleteId,
    string Name,
    int SeasonRank,
    int TotalChampionshipPointsThousandths,
    int TotalStageScoreThousandths,
    int TotalBaseScoreThousandths,
    int StageWins,
    int RoundWins,
    bool IsChampion);
