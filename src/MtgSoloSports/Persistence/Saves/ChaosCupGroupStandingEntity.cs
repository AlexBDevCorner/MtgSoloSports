namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Chaos Cup group standing row per entrant per staged group (MSS-099).
/// Ranks 1..32 are by group stage score (fixed-point thousandths sums of final round
/// points across the group's 16 rounds) with deterministic tie-breaking; no
/// championship points or career bonus are generated. <see cref="RoundPlaceCountsJson"/>
/// is a compact JSON array of 32 round-placement counts for auditing without scanning
/// payloads. <see cref="IsAdvanced"/> freezes advancement: ranks 1..16 in nonfinal
/// stages advance (the next stage draw must contain exactly those athletes); the Final
/// stage (single group) holds official places 1..32 with <see cref="IsAdvanced"/> false
/// everywhere. Uniqueness on (SourceSeasonId, StageIndex, SaveAthleteId) rejects
/// duplicate stage membership; uniqueness on (SourceSeasonId, StageIndex, GroupNumber,
/// GroupRank) rejects duplicate ranks and advancement.
/// </summary>
public sealed class ChaosCupGroupStandingEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int StageIndex { get; set; }

    public int GroupNumber { get; set; }

    public int SaveAthleteId { get; set; }

    public int GroupRank { get; set; }

    public int GroupScoreThousandths { get; set; }

    public int BaseScoreThousandths { get; set; }

    public int RoundWins { get; set; }

    public string RoundPlaceCountsJson { get; set; } = "[]";

    public int SportingColor { get; set; }

    public bool IsAdvanced { get; set; }
}
