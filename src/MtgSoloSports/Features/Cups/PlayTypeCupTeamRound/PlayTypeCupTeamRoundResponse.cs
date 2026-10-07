using MtgSoloSports.Features.History.EventRounds;

namespace MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;

/// <summary>
/// One persisted Type Cup tournament round plus tournament progress. Stage
/// identity exposes the current tournament stage (Final, Qualification Group N
/// of G, Single field for legacy), the persisted qualification group (0 outside
/// qualification), and the athlete rank group/round within the stage. Extra
/// fields are additive: older callers ignore them.
/// </summary>
public sealed record PlayTypeCupTeamRoundResponse(
    EventRoundView Round,
    int RoundsPlayed,
    int TotalRounds,
    bool IsComplete,
    string TournamentStage = "Single field",
    int TournamentPhase = 0,
    int QualificationGroup = 0,
    int RankGroup = 0,
    int RoundInGroup = 0);
