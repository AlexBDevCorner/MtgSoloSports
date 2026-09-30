using MtgSoloSports.Features.History.EventRounds;

namespace MtgSoloSports.Features.Cups.PlayColorCupTeamRound;

public sealed record PlayColorCupTeamRoundResponse(
    EventRoundView Round,
    int RoundsPlayed,
    int TotalRounds,
    bool IsComplete);
