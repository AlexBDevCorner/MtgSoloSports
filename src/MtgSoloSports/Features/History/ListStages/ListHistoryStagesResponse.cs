namespace MtgSoloSports.Features.History.ListStages;

/// <summary>
/// Immutable presentation DTO for the stage list of one historical competition.
/// </summary>
public sealed record ListHistoryStagesResponse(
    Guid SaveId,
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    IReadOnlyList<HistoryStageSummary> Stages);
