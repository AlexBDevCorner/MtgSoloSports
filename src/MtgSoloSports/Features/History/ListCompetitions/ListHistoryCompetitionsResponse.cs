namespace MtgSoloSports.Features.History.ListCompetitions;

/// <summary>
/// Immutable presentation DTO for the competition list of one historical season.
/// </summary>
public sealed record ListHistoryCompetitionsResponse(
    Guid SaveId,
    int SeasonNumber,
    bool IsSeasonComplete,
    IReadOnlyList<HistoryCompetitionSummary> Competitions);
