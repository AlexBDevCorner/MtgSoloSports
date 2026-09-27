namespace MtgSoloSports.Features.History.GetStageStandings;

/// <summary>
/// Immutable presentation DTO for one historical stage's standings.
/// An empty <c>Standings</c> list means the stage has no persisted standings yet.
/// </summary>
public sealed record GetHistoryStageStandingsResponse(
    Guid SaveId,
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    int StageNumber,
    bool IsStageComplete,
    string StageChecksum,
    IReadOnlyList<HistoryStageStandingEntry> Standings);
