namespace MtgSoloSports.Features.Simulation.CompleteSeason;

/// <summary>
/// Immutable lightweight DTO for completing the remaining global stages of the
/// current season. Contains only counts, cursors and RNG boundaries so bulk
/// mode never constructs expensive per-round animation DTOs; sporting results
/// live in persisted rounds/standings and are replayed via existing queries.
/// <see cref="GlobalStageAfter"/> is <c>StagesPerSeason + 1</c> (33) when the
/// season is complete.
/// </summary>
public sealed record CompleteSeasonResponse(
    Guid SaveId,
    int SeasonNumber,
    int StagesCompleted,
    int GlobalStageBefore,
    int GlobalStageAfter,
    bool IsSeasonComplete,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    CompleteSeasonProgress Progress);
