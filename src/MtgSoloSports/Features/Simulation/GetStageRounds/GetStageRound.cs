namespace MtgSoloSports.Features.Simulation.GetStageRounds;

/// <summary>
/// One persisted round inside a stage: immutable replay facts plus the
/// payload checksum so presentation can prove it replays identical data.
/// </summary>
public sealed record GetStageRound(
    int RoundNumber,
    int RulesVersion,
    string PayloadChecksum,
    IReadOnlyList<GetStageRoundPlacement> Placements);
