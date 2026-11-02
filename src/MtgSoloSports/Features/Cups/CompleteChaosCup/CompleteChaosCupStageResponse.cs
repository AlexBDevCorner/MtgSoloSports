namespace MtgSoloSports.Features.Cups.CompleteChaosCup;

/// <summary>
/// Stage-scoped Chaos Cup fast-forward result (MSS-103). Completes every group
/// of one already-drawn stage in canonical group order by reusing the one-group
/// complete action, resuming partial groups and returning persisted draws
/// untouched. Built only from persisted rounds and standings so replay never
/// resimulates; group scores reset per stage with no league side effects.
/// </summary>
public sealed record CompleteChaosCupStageResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int StageIndex,
    int GroupCount,
    int EntrantCount,
    int GroupsCompleted,
    int RoundsPlayedThisCall,
    int RoundsPlayedTotal,
    int TotalRounds,
    bool IsComplete,
    IReadOnlyList<CompleteChaosCupStageResponse.StageGroupProgress> Groups)
{
    public sealed record StageGroupProgress(
        int GroupNumber,
        int RoundsPlayed,
        bool IsComplete);
}
