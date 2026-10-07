namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Type Cup team championship standing row per participating creature
/// type per completed even source season. The team score is the sum of the type's
/// four legs' group scores across the four rank groups; ranking uses the same
/// deterministic tie-breaking inputs as the pure team-event kernel (group-rank
/// counts, aggregated round-place counts, raw base totals, seeded draw) with a
/// dynamically varying team count N.
/// <see cref="Medal"/> persists Gold (1), Silver (2) and Bronze (3) for team
/// ranks 1..3; all other teams store 0. The official team championship
/// (rank 1) is additionally persisted as four <c>Honours</c> rows (one per
/// winning-team member) so career prestige counts it as an other-major honour.
/// <see cref="GroupPlaceCountsJson"/> and <see cref="RoundPlaceCountsJson"/>
/// are compact JSON arrays of N counts retained for deterministic auditing
/// without scanning round payloads.
/// Tournament identity (MSS-061) uses separate fields: <see cref="TournamentPhase"/>
/// (0 legacy single-field, 1 qualification group table, 2 Final) and
/// <see cref="QualificationGroup"/> (0 for legacy/Final, 1..G for qualification).
/// Legacy rows store 0/0; qualification tables store 1/G per group and the Final
/// stores 2/0 so qualification and Final standings never collide.
/// </summary>
public sealed class TypeCupTeamStandingEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int TournamentPhase { get; set; }

    public int QualificationGroup { get; set; }

    public string CreatureType { get; set; } = string.Empty;

    public int TeamRank { get; set; }

    public int TeamScoreThousandths { get; set; }

    public int TeamBaseThousandths { get; set; }

    public int GroupWins { get; set; }

    public int RoundWins { get; set; }

    public string GroupPlaceCountsJson { get; set; } = "[]";

    public string RoundPlaceCountsJson { get; set; } = "[]";

    public int Medal { get; set; }
}
