namespace MtgSoloSports.Features.History.ListEventRounds;

public sealed record ListHistoryEventRoundsResponse(
    Guid SaveId,
    int SeasonNumber,
    string Event,
    IReadOnlyList<HistoryEventRoundSummary> Rounds);
