namespace MtgSoloSports.Features.History.ListSeasons;

/// <summary>
/// Normalized season summary for history navigation. Built only from
/// <c>Seasons</c>, <c>Leagues</c>, <c>Stages</c> and round counts; never reads
/// or decompresses round payloads.
/// </summary>
public sealed record HistorySeasonSummary(
    int SeasonNumber,
    bool HasSuperleague,
    bool IsComplete,
    int CompetitionCount,
    int CompletedStages,
    int TotalRounds);
