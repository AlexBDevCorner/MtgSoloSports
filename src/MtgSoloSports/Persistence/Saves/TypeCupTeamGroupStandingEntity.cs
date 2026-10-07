namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Type Cup team leg (group) standing row per selected athlete
/// per completed even source season. Each of the four rank groups holds N athletes
/// (one per participating creature type at the same selection rank, where N is the
/// dynamically varying team count); ranks 1..N are by group stage score with
/// deterministic tie-breaking. Scores are fixed-point thousandths sums of final
/// round points across the eight group rounds; no championship points or career
/// bonus are generated here.
/// <see cref="RoundPlaceCountsJson"/> is a compact JSON array of N
/// round-placement counts retained for deterministic auditing without scanning
/// round payloads. <see cref="GroupNumber"/> always equals
/// <see cref="SelectionRank"/> (1..4) so a wrong-rank placement is detectable.
/// Tournament identity (MSS-061) uses separate fields: <see cref="TournamentPhase"/>
/// (0 legacy single-field, 1 qualification, 2 Final) and
/// <see cref="QualificationGroup"/> (0 for legacy/Final, 1..G for qualification).
/// Legacy rows store 0/0; qualification legs store 1/G and Final legs store 2/0
/// so an athlete who qualifies holds two leg rows that never collide.
/// </summary>
public sealed class TypeCupTeamGroupStandingEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int TournamentPhase { get; set; }

    public int QualificationGroup { get; set; }

    public int GroupNumber { get; set; }

    public int SaveAthleteId { get; set; }

    public int GroupRank { get; set; }

    public int GroupScoreThousandths { get; set; }

    public int BaseScoreThousandths { get; set; }

    public int RoundWins { get; set; }

    public string RoundPlaceCountsJson { get; set; } = "[]";

    public string CreatureType { get; set; } = string.Empty;

    public int SelectionRank { get; set; }
}
