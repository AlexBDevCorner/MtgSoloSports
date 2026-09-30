using MtgSoloSports.Features.History.EventRounds;

namespace MtgSoloSports.Features.Cups.PlayColorCupIndividualRound;

public sealed record PlayColorCupIndividualRoundResponse(
    EventRoundView Round,
    int RoundsPlayed,
    int TotalRounds,
    bool IsComplete);
