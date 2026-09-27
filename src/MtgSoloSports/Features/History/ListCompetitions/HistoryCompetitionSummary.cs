namespace MtgSoloSports.Features.History.ListCompetitions;

/// <summary>
/// Normalized competition (league) summary for one historical season.
/// Built only from <c>Leagues</c>, <c>Stages</c> and <c>SeasonStandings</c>;
/// never reads round payloads.
/// </summary>
public sealed record HistoryCompetitionSummary(
    int LeagueId,
    string Name,
    string Kind,
    int SportingColor,
    string SportingColorName,
    int StageCount,
    int CompletedStages,
    int TotalRounds,
    bool HasFinalTable);
