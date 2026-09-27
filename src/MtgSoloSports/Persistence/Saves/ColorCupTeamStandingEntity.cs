namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Color Cup team championship standing row per sporting color
/// per completed odd source season. The team score is the sum of the color's
/// four legs' group scores across the four rank groups; ranking uses the same
/// deterministic tie-breaking inputs as the pure team-event kernel (group-rank
/// counts, aggregated round-place counts, raw base totals, seeded draw).
/// <see cref="Medal"/> persists Gold (1), Silver (2) and Bronze (3) for team
/// ranks 1..3; all other teams store 0. The official team championship
/// (rank 1) is additionally persisted as four <c>Honours</c> rows (one per
/// winning-team member) so career prestige counts it as an other-major honour.
/// <see cref="GroupPlaceCountsJson"/> and <see cref="RoundPlaceCountsJson"/>
/// are compact JSON arrays of eight counts retained for deterministic auditing
/// without scanning round payloads.
/// </summary>
public sealed class ColorCupTeamStandingEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int SportingColor { get; set; }

    public int TeamRank { get; set; }

    public int TeamScoreThousandths { get; set; }

    public int TeamBaseThousandths { get; set; }

    public int GroupWins { get; set; }

    public int RoundWins { get; set; }

    public string GroupPlaceCountsJson { get; set; } = "[]";

    public string RoundPlaceCountsJson { get; set; } = "[]";

    public int Medal { get; set; }
}
