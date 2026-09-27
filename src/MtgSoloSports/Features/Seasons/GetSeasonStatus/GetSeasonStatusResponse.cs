namespace MtgSoloSports.Features.Seasons.GetSeasonStatus;

/// <summary>
/// Immutable read model exposing the explicit season lifecycle phase plus the
/// legal next actions. Season 1 uses the special inaugural chain; Season 2+
/// uses automatic movement plus qualifier. Both converge on Rebalanced, the
/// post-rebalance Cup extension point, before StartNextSeason.
/// </summary>
public sealed record GetSeasonStatusResponse(
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
    bool ReadyToStartNextSeason,
    string ExpectedCup,
    IReadOnlyList<string> LegalNextActions,
    string NextActionDetail);
