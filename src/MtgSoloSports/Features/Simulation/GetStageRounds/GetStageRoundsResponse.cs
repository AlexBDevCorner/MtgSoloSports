namespace MtgSoloSports.Features.Simulation.GetStageRounds;

/// <summary>
/// Immutable presentation DTO for all persisted rounds in one league stage.
/// Built only from persisted round rows plus save-owned card snapshots; reads
/// never resimulate and never mutate RNG state. An empty <see cref="Rounds"/>
/// list means no rounds have been simulated yet for the requested stage.
/// </summary>
public sealed record GetStageRoundsResponse(
    Guid SaveId,
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    int StageNumber,
    int CompletedRounds,
    int RoundsPerStage,
    bool IsStageComplete,
    IReadOnlyList<GetStageRound> Rounds);
