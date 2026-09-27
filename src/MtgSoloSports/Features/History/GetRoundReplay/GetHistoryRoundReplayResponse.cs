namespace MtgSoloSports.Features.History.GetRoundReplay;

/// <summary>
/// Immutable presentation DTO for one exact historical round. Built only from
/// the single persisted round payload plus save-owned card snapshots; reads
/// never resimulate, never consume RNG and never mutate save state. The
/// <c>Placements</c> shape matches the live round presentation model so the
/// same UI renders live and historical results.
/// </summary>
public sealed record GetHistoryRoundReplayResponse(
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
    IReadOnlyList<HistoryRoundPlacement> Placements);
