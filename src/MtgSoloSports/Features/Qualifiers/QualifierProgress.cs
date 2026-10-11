using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Single authoritative qualifier progress model (MSS-075).
/// Dashboard, Qualifiers overview/detail, Live, season lifecycle and rebalance
/// prerequisites must all derive completion from the same persisted facts:
/// per-event standings plus per-event rounds. An event is complete only when
/// its field size, round count and winner count all match the versioned rules;
/// standings without full rounds (or vice versa) are corruption and throw with
/// an actionable message instead of being silently counted as complete or
/// pending. Totals are never hardcoded: 1 event / 16 rounds for single-feeder
/// saves, 17 events / 272 rounds for three-tier saves, derived from
/// <see cref="RulesV1"/>.
/// </summary>
public sealed record QualifierProgress(
    int TotalEvents,
    int CompletedEvents,
    int TotalRounds,
    int CompletedRounds,
    int TotalStandings,
    int CompletedStandings)
{
    public bool IsComplete => CompletedEvents == TotalEvents
        && CompletedRounds == TotalRounds
        && CompletedStandings == TotalStandings
        && TotalEvents > 0;
}
