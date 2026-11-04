namespace MtgSoloSports.SaveSnapshot;

/// <summary>
/// Per-season stage coverage reported for a working snapshot.
/// </summary>
public sealed record SeasonCoverage(
    int SeasonNumber,
    bool HasSuperleague,
    bool IsComplete,
    int StageCount,
    int CompletedStages,
    int RoundCount);
