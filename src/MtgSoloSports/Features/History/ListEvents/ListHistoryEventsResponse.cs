namespace MtgSoloSports.Features.History.ListEvents;

public sealed record ListHistoryEventsResponse(
    Guid SaveId,
    int SeasonNumber,
    IReadOnlyList<HistoryEventSummary> Events);
