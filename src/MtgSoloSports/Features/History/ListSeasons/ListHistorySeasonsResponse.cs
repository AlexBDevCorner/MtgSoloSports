namespace MtgSoloSports.Features.History.ListSeasons;

/// <summary>
/// Immutable presentation DTO for the season list in history navigation.
/// </summary>
public sealed record ListHistorySeasonsResponse(
    Guid SaveId,
    IReadOnlyList<HistorySeasonSummary> Seasons);
