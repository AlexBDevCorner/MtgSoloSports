namespace MtgSoloSports.Features.History.GetSeasonTable;

/// <summary>
/// Immutable presentation DTO for one historical season's final table.
/// </summary>
public sealed record GetHistorySeasonTableResponse(
    Guid SaveId,
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    bool IsSeasonComplete,
    string SeasonChecksum,
    IReadOnlyList<HistorySeasonTableEntry> Standings);
