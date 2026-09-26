namespace MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;

/// <summary>
/// Immutable presentation DTO for completing the current global stage across
/// all active leagues. Built only from persisted stage completions so replay
/// never resimulates. <see cref="GlobalStageAfter"/> is the next global stage
/// (33 when the season is complete). <see cref="IsSeasonComplete"/> is true
/// only after Stage 32 is complete for every active league and deterministic
/// season standings are committed.
/// </summary>
public sealed record CompleteStageForAllLeaguesResponse(
    Guid SaveId,
    int SeasonNumber,
    int CompletedStage,
    int GlobalStageAfter,
    bool IsSeasonComplete,
    IReadOnlyList<BulkLeagueStageResult> Leagues);
