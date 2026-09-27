namespace MtgSoloSports.Features.Seasons.AdvanceToNextEvent;

/// <summary>
/// Immutable presentation DTO for one AdvanceToNextEvent step. Exactly one
/// legal event executes per call in canonical postseason order
/// (stage, movement, qualifier, rebalance, next season), behaviorally
/// equivalent to invoking the underlying focused operation directly.
/// Each step exposes its inspectable event boundary via the status read model.
/// </summary>
public sealed record AdvanceToNextEventResponse(
    Guid SaveId,
    string ExecutedAction,
    string ExecutedDetail,
    int CurrentSeasonNumber,
    string ComputedPhase,
    string PersistedPhase,
    int? SourceSeasonNumber,
    int? NextSeasonNumber,
    bool IsInauguralTransition,
    int GlobalStage,
    bool IsCurrentSeasonComplete,
    IReadOnlyList<string> LegalNextActions,
    string NextActionDetail);
