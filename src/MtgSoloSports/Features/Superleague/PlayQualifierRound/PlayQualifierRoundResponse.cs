using MtgSoloSports.Features.History.EventRounds;

namespace MtgSoloSports.Features.Superleague.PlayQualifierRound;

public sealed record PlayQualifierRoundResponse(
    EventRoundView Round,
    int RoundsPlayed,
    int TotalRounds,
    bool IsComplete);
