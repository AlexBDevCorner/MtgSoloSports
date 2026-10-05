namespace MtgSoloSports.Features.History.ListCompetitions;

/// <summary>
/// Normalized competition (league) summary for one historical season.
/// Built only from <c>Leagues</c>, <c>Stages</c> and <c>SeasonStandings</c>;
/// never reads round payloads. Feeder division and league level identify the
/// tier explicitly without parsing league names.
/// </summary>
public sealed record HistoryCompetitionSummary(
    int LeagueId,
    string Name,
    string Kind,
    int FeederDivision,
    string LeagueLevel,
    int SportingColor,
    string SportingColorName,
    int StageCount,
    int CompletedStages,
    int TotalRounds,
    bool HasFinalTable);
