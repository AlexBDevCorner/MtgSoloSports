using MtgSoloSports.Features.Seasons.SeasonLifecycle;

namespace MtgSoloSports.Features.Seasons.AdvanceToNextEvent;

/// <summary>
/// Immutable presentation DTO for one AdvanceToNextEvent step. Exactly one
/// legal event executes per call in canonical postseason order
/// (stage, movement, qualifier, rebalance, Cup selection, Cup individual for
/// Color, Cup team, next season), behaviorally equivalent to invoking the
/// underlying focused operation directly. Each step exposes its inspectable
/// event boundary via the status read model.
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
    bool SeasonComplete,
    bool MovementResolved,
    bool QualifierResolved,
    bool Rebalanced,
    bool CupSelectionResolved,
    bool CupIndividualResolved,
    bool CupTeamResolved,
    bool CupComplete,
    bool ReadyToStartNextSeason,
    string ExpectedCup,
    IReadOnlyList<string> LegalNextActions,
    string NextActionDetail,
    PostseasonEvents.SeasonEventProgress? EventProgress);
