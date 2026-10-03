namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One official podium honour per competition top-three finish. MSS-022 persisted
/// the eight feeder-league championships plus the Superleague championship
/// (from Season 2) as durable honours so career comparison and record-chasing
/// never rescan season tables for common views. MSS-047 expands honours to
/// podiums: each league season contributes three honours (1st/2nd/3rd) and each
/// Cup podium contributes honours for its 2nd/3rd finishers with distinct kinds.
/// Podium and placement statistics remain in <c>AthleteCareers</c> /
/// <c>AthleteSeasonSummaries</c>; this table stores official honours (titles plus
/// runner-up and third-place finishes). Cup honours are added by Cup tasks
/// without rewriting existing rows.
/// Rebuildable from <c>SeasonStandings</c> (<c>SeasonRank</c> 1..3) plus
/// <c>Leagues</c> and from Cup standings tables without touching round payloads.
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
