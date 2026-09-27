namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One official championship honour per league-season champion. MSS-022 persists
/// the eight feeder-league championships plus the Superleague championship
/// (from Season 2) as durable honours so career comparison and record-chasing
/// never rescan season tables for common views. Podium and placement
/// statistics remain in <c>AthleteCareers</c> / <c>AthleteSeasonSummaries</c>;
/// this table stores only official major titles (feeder + Superleague).
/// Cup honours are added by later Cup tasks without rewriting these rows.
/// Rebuildable from <c>SeasonStandings</c> (<c>IsChampion</c>) plus
/// <c>Leagues</c> without touching round payloads.
/// </summary>
public sealed class HonourEntity
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int SeasonNumber { get; set; }

    public int LeagueId { get; set; }

    public string LeagueName { get; set; } = string.Empty;

    public int LeagueKind { get; set; }

    public int SaveAthleteId { get; set; }

    public int Kind { get; set; }
}
