namespace MtgSoloSports.Features.History.GetEventTeamStandings;

public sealed record HistoryEventTeamStandingsResponse(
    Guid SaveId,
    int SeasonNumber,
    string Event,
    bool IsFinal,
    int GroupsCompleted,
    IReadOnlyList<HistoryEventTeamRow> Teams);
