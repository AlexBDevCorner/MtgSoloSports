namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Color Cup team leg (group) standing row per selected athlete
/// per completed odd source season. Each of the four rank groups holds eight
/// athletes (one per sporting color at the same selection rank); ranks 1..8
/// are by group stage score with deterministic tie-breaking. Scores are
/// fixed-point thousandths sums of final round points across the eight group
/// rounds; no championship points or career bonus are generated here.
/// <see cref="RoundPlaceCountsJson"/> is a compact JSON array of eight
/// round-placement counts retained for deterministic auditing without scanning
/// round payloads. <see cref="GroupNumber"/> always equals
/// <see cref="SelectionRank"/> (1..4) so a wrong-rank placement is detectable.
/// </summary>
public sealed class ColorCupTeamGroupStandingEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int GroupNumber { get; set; }

    public int SaveAthleteId { get; set; }

    public int GroupRank { get; set; }

    public int GroupScoreThousandths { get; set; }

    public int BaseScoreThousandths { get; set; }

    public int RoundWins { get; set; }

    public string RoundPlaceCountsJson { get; set; } = "[]";

    public int SportingColor { get; set; }

    public int SelectionRank { get; set; }
}
