namespace MtgSoloSports.Features.History.ListStages;

/// <summary>
/// Normalized stage summary for history navigation. Built only from
/// <c>Stages</c>, <c>StageStandings</c> and round identity counts; never reads
/// or decompresses round payloads.
/// </summary>
public sealed record HistoryStageSummary(
    int StageNumber,
    int CompletedRounds,
    int RoundsPerStage,
    bool IsComplete,
    bool HasStandings,
    int RoundCount);
