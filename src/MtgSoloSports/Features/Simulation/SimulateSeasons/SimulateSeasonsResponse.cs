namespace MtgSoloSports.Features.Simulation.SimulateSeasons;

/// <summary>
/// Immutable lightweight DTO for an explicit multi-season fast simulation.
/// Carries only counts, cursors and RNG boundaries; per-round placements,
/// per-stage standings and qualifier details stay in persisted rows so bulk
/// mode never constructs expensive animation DTOs.
/// </summary>
public sealed record SimulateSeasonsResponse(
    Guid SaveId,
    int SeasonsRequested,
    int SeasonsCompleted,
    int StagesCompleted,
    int PostseasonStepsCompleted,
    int StartSeasonNumber,
    int EndSeasonNumber,
    int GlobalStage,
    string ComputedPhase,
    bool IsCurrentSeasonComplete,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    SimulateSeasonsProgress Progress);
