namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable qualifier standing row per athlete per postseason transition
/// per qualifier event. The Superleague qualifier field is exactly 32 athletes
/// (8 incumbents from ranks 17-24 plus 24 challengers from F1 ranks 2-4);
/// each feeder qualifier field is exactly 16 athletes (8 incumbents + 8
/// challengers per color per boundary). The top 8 by qualifier stage score
/// qualify/remain in the higher tier (<see cref="IsQualified"/>). Scores are
/// fixed-point thousandths sums of final round points across the 16 qualifier
/// rounds; no championship points or career bonus are generated here, so
/// normal league season totals are untouched.
/// <see cref="RoundPlaceCountsJson"/> is a compact JSON array of round-placement
/// counts (32 for Superleague, 16 for feeder) retained for deterministic
/// tie-breaking without scanning round payloads.
/// MSS-058 adds <see cref="QualifierBoundary"/> plus
/// <see cref="QualifierSportingColor"/> so multiple qualifiers per transition
/// persist safely without colliding.
/// </summary>
public sealed class QualifierStandingEntity
{
    public int Id { get; set; }

    public int FromSeasonId { get; set; }

    public int ToSeasonId { get; set; }

    /// <summary>
    /// Adjacent-tier boundary: 0 Superleague↔F1, 1 F1↔F2, 2 F2↔F3.
    /// </summary>
    public int QualifierBoundary { get; set; }

    /// <summary>
    /// Sporting color for feeder boundaries (0..7); -1 for Superleague.
    /// </summary>
    public int QualifierSportingColor { get; set; }

    public int SaveAthleteId { get; set; }

    public int QualifierRank { get; set; }

    public int QualifierScoreThousandths { get; set; }

    public int BaseScoreThousandths { get; set; }

    public int RoundWins { get; set; }

    public string RoundPlaceCountsJson { get; set; } = "[]";

    public bool IsQualified { get; set; }

    public int Role { get; set; }

    public int FromLeagueId { get; set; }

    public int FromSeasonRank { get; set; }

    public int SportingColor { get; set; }
}
