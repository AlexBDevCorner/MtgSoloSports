namespace MtgSoloSports.Features.Simulation.CompleteStage;

/// <summary>
/// Immutable presentation DTO for a completed league stage. Built only from
/// persisted rounds plus persisted stage standings so replay never resimulates.
/// <see cref="NextStageNumber"/> is null when the completed stage is the
/// season's final stage; season finalization arrives in a later slice.
/// </summary>
public sealed record CompleteStageResponse(
    Guid SaveId,
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    int StageNumber,
    int RulesVersion,
    int CompletedRounds,
    string StageChecksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    int? NextStageNumber,
    IReadOnlyList<CompleteStageStanding> Standings);
