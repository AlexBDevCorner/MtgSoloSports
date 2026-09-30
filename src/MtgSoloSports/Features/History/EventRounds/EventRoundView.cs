using MtgSoloSports.Features.History.GetRoundReplay;

namespace MtgSoloSports.Features.History.EventRounds;

/// <summary>
/// One persisted postseason event round in the same presentation shape as a
/// league round replay, so the web reveal renders it unchanged. Built only
/// from the stored payload; never resimulates.
/// </summary>
public sealed record EventRoundView(
    int SeasonNumber,
    string Event,
    string Title,
    int? Group,
    int RoundNumber,
    int RulesVersion,
    string PayloadChecksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    IReadOnlyList<HistoryRoundPlacement> Placements);
