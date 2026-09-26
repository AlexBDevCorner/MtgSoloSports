namespace MtgSoloSports.Features.Leagues.SeasonTable;

/// <summary>
/// Immutable presentation DTO for a completed league season table.
/// Read only from persisted final <c>SeasonStanding</c> rows (including the
/// champion) so history, promotion and selection consumers share one durable
/// summary; replay never resimulates.
/// </summary>
public sealed record GetSeasonTableResponse(
    Guid SaveId,
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    bool IsSeasonComplete,
    string SeasonChecksum,
    IReadOnlyList<SeasonTableEntry> Standings);
