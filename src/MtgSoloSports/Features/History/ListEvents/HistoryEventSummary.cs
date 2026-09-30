namespace MtgSoloSports.Features.History.ListEvents;

public sealed record HistoryEventSummary(
    string Event,
    string Title,
    int RoundsPlayed,
    int TotalRounds,
    int GroupCount,
    int RoundsPerGroup,
    bool IsComplete);
