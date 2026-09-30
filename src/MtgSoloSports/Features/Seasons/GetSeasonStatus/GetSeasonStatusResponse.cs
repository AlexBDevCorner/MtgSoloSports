using MtgSoloSports.Features.Seasons.SeasonLifecycle;

namespace MtgSoloSports.Features.Seasons.GetSeasonStatus;

/// <summary>
/// Immutable read model exposing the explicit season lifecycle phase plus the
/// legal next actions. Season 1 uses the special inaugural chain; Season 2+
/// uses automatic movement plus qualifier. Both converge on Rebalanced, then run
/// the alternating post-season Cup (odd Color with selection, individual and
/// team; even Type with selection and team) before StartNextSeason. Each Cup
/// step is an explicit inspectable Next Event boundary.
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
    bool CupSelectionResolved,
    bool CupIndividualResolved,
    bool CupTeamResolved,
    bool CupComplete,
    bool ReadyToStartNextSeason,
    string ExpectedCup,
    IReadOnlyList<string> LegalNextActions,
    string NextActionDetail,
    PostseasonEvents.SeasonEventProgress? EventProgress);
