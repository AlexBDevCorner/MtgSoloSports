namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable qualifier standing row per athlete per postseason transition.
/// The qualifier field is exactly 32 athletes (8 Superleague incumbents from
/// ranks 17-24 plus 24 feeder challengers from ranks 2-4 across all eight
/// leagues); the top 8 by qualifier stage score qualify/remain in the next
/// Superleague (<see cref="IsQualified"/>). Scores are fixed-point thousandths
/// sums of final round points across the 16 qualifier rounds; no championship
/// points or career bonus are generated here, so normal league season totals
/// are untouched. <see cref="RoundPlaceCountsJson"/> is a compact JSON array of
/// 32 round-placement counts retained for deterministic tie-breaking without
/// scanning round payloads.
/// </summary>
public sealed class QualifierStandingEntity
{
    public int Id { get; set; }

    public int FromSeasonId { get; set; }

    public int ToSeasonId { get; set; }

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
