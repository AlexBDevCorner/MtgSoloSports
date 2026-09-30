namespace MtgSoloSports.Features.Seasons.SeasonLifecycle;

/// <summary>
/// Computed lifecycle state for a save. The computed phase is authoritative and
/// derived from persisted seasons, standings, movements, qualifier, roster and
/// Cup rows; the persisted <c>SaveMetadata.Phase</c> text is informational and is
/// kept in sync by mutating slices. Legal next actions expose exactly one
/// inspectable event boundary at a time so callers never need internal ordering.
/// </summary>
public sealed record SeasonLifecycleSnapshot(
    Guid SaveId,
    int CurrentSeasonNumber,
    string PersistedPhase,
    string ComputedPhase,
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
    PostseasonEvents.SeasonEventProgress? EventProgress = null);
