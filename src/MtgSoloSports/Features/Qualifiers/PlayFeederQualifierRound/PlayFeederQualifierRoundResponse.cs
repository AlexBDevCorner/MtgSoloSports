using MtgSoloSports.Features.History.EventRounds;

namespace MtgSoloSports.Features.Qualifiers.PlayFeederQualifierRound;

/// <summary>
/// Immutable result of playing exactly one feeder qualifier round.
/// Built only from persisted state; the round payload is authoritative and
/// replay uses the same <see cref="EventRoundView"/> shape as Superleague
/// qualifier rounds so the Live reveal reuses one component for 16- and
/// 32-athlete fields. Title carries the feeder boundary/color identity so a
/// phase-wide round count is never presented under an individual event.
/// </summary>
public sealed record PlayFeederQualifierRoundResponse(
    EventRoundView Round,
    int RoundsPlayed,
    int TotalRounds,
    bool IsComplete,
    string Boundary,
    int SportingColor,
    string SportingColorName,
    int FromSeasonNumber,
    int ToSeasonNumber);
