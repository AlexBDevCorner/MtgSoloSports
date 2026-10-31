namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One frozen Chaos Cup entrant per edition (MSS-099). Freezes athlete identity,
/// permanent sporting color and source-season division (league tier or Pool) before
/// promotion/rebalancing so later movement never rewrites the field.
/// <see cref="SourceLeagueId"/> is null for Pool invitations; otherwise it records
/// the source-season league alongside <see cref="SourceLeagueKind"/> and
/// <see cref="SourceFeederDivision"/> for auditing. Uniqueness on
/// (SourceSeasonId, SaveAthleteId) rejects duplicate entrants.
/// </summary>
public sealed class ChaosCupEntrantEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int SaveAthleteId { get; set; }

    public int SportingColor { get; set; }

    public int? SourceLeagueId { get; set; }

    public int SourceLeagueKind { get; set; }

    public int SourceFeederDivision { get; set; }

    public bool IsPoolInvite { get; set; }
}
