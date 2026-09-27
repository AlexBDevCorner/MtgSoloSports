namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Color Cup individual standing row per selected athlete per
/// completed odd source season. The field is exactly the 32 selected athletes
/// (8 colors x 4); ranks 1..32 are by Cup stage score with deterministic
/// tie-breaking. <see cref="Medal"/> persists Gold (1), Silver (2) and Bronze
/// (3) for Cup ranks 1..3; all other athletes store 0. Scores are fixed-point
/// thousandths sums of final round points across the 16 Cup rounds; no
/// championship points or career bonus are generated here, so normal league
/// season totals are untouched. <see cref="RoundPlaceCountsJson"/> is a compact
/// JSON array of 32 round-placement counts retained for deterministic
/// auditing without scanning round payloads. The official individual
/// championship (rank 1) is additionally persisted as a
/// <c>Honours</c> row so career prestige counts it as an other-major honour.
/// </summary>
public sealed class ColorCupIndividualStandingEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int SaveAthleteId { get; set; }

    public int CupRank { get; set; }

    public int CupScoreThousandths { get; set; }

    public int BaseScoreThousandths { get; set; }

    public int RoundWins { get; set; }

    public string RoundPlaceCountsJson { get; set; } = "[]";

    public int Medal { get; set; }

    public int SportingColor { get; set; }

    public int SelectionRank { get; set; }
}
