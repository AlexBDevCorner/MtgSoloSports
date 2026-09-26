namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One transactional season summary row per athlete per season. Active athletes
/// accumulate stage results as stages complete and receive their final
/// <c>SeasonRank</c> / champion flag at season finalization; common-pool
/// athletes keep an explicit inactive row (all zeros, <c>WasActive</c> false)
/// so career history distinguishes "inactive season" from "missing data".
/// Rebuildable from <c>StageStanding</c> / <c>SeasonStanding</c> /
/// <c>SeasonMembership</c> without touching round payloads.
/// All sporting values are fixed-point thousandths integers.
/// </summary>
public sealed class AthleteSeasonSummaryEntity
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int SeasonNumber { get; set; }

    public int SaveAthleteId { get; set; }

    public int? LeagueId { get; set; }

    public string? LeagueName { get; set; }

    public int? LeagueKind { get; set; }

    public bool WasActive { get; set; }

    public int RoundWins { get; set; }

    public int StageWins { get; set; }

    public int StageSeconds { get; set; }

    public int StageThirds { get; set; }

    public int? SeasonRank { get; set; }

    public bool IsChampion { get; set; }

    public int EarnedBonusThousandths { get; set; }

    public int TotalChampionshipPointsThousandths { get; set; }

    public int TotalStageScoreThousandths { get; set; }

    public int TotalBaseScoreThousandths { get; set; }
}
