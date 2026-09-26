namespace MtgSoloSports.Features.Simulation.AdvanceRound;

/// <summary>
/// Immutable presentation DTO for one persisted round. Built only from the
/// persisted round payload so instant and future animated UI replay identical
/// facts without resimulation.
/// </summary>
public sealed record AdvanceRoundResponse(
    Guid SaveId,
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    int StageNumber,
    int RoundNumber,
    int RulesVersion,
    string PayloadChecksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    IReadOnlyList<AdvanceRoundPlacement> Placements);
