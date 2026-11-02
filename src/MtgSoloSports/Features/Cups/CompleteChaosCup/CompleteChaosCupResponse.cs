namespace MtgSoloSports.Features.Cups.CompleteChaosCup;

/// <summary>
/// Tournament-wide Chaos Cup fast-forward result (MSS-103). Completes every
/// remaining stage to the Final in canonical stage and group order, drawing
/// each missing stage once via the persisted stage-draw action and completing
/// groups via the one-group complete action. Resumes partial groups and
/// persisted draws; retries consume no additional RNG or bonus. Built only
/// from persisted draws, rounds and standings so replay never resimulates.
/// </summary>
public sealed record CompleteChaosCupResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int StagesCompleted,
    int StagesTotal,
    int GroupsCompleted,
    int TotalGroups,
    int RoundsPlayedThisCall,
    int RoundsPlayedTotal,
    int TotalRounds,
    bool IsComplete,
    IReadOnlyList<CompleteChaosCupResponse.CupStageProgress> Stages)
{
    public sealed record CupStageProgress(
        int StageIndex,
        int GroupCount,
        int GroupsCompleted,
        bool IsComplete);
}
