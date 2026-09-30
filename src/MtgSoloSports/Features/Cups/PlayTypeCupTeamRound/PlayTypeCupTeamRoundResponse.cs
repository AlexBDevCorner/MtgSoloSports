using MtgSoloSports.Features.History.EventRounds;

namespace MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;

public sealed record PlayTypeCupTeamRoundResponse(
    EventRoundView Round,
    int RoundsPlayed,
    int TotalRounds,
    bool IsComplete);
