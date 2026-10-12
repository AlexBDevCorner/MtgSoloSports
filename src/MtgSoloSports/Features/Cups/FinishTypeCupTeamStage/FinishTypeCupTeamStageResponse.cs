using MtgSoloSports.Features.History.EventRounds;

namespace MtgSoloSports.Features.Cups.FinishTypeCupTeamStage;

/// <summary>
/// Stage-scoped fast-forward result: the last persisted round of the finished
/// canonical stage plus tournament progress. Finishes exactly one qualification
/// group or the Final atomically under the per-save lock; never crosses into
/// the next stage. Extra fields are additive.
/// </summary>
public sealed record FinishTypeCupTeamStageResponse(
    EventRoundView Round,
    int RoundsPlayed,
    int TotalRounds,
    bool IsComplete,
    string TournamentStage,
    int TournamentPhase,
    int QualificationGroup,
    int RankGroup,
    int RoundInGroup,
    int StageRoundsPlayed,
    int StageTotalRounds,
    int RoundsPlayedThisCall);
