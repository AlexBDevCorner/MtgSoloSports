namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One transactional career projection row per save athlete. Aggregated from
/// authoritative <c>StageStanding</c> / <c>SeasonStanding</c> /
/// <c>SeasonMembership</c> history in the same transaction as stage and season
/// completions, so ordinary athlete profiles never decompress round payloads.
/// Rebuildable: <c>Features/Athletes/Projections</c> can recompute every field
/// from those authoritative tables.
/// All sporting values are fixed-point thousandths integers.
/// </summary>
public sealed class AthleteCareerEntity
{
    public int Id { get; set; }

    public int SaveAthleteId { get; set; }

    public int SeasonsActive { get; set; }

    public int? CurrentLeagueId { get; set; }

    public string? CurrentLeagueName { get; set; }

    public int? CurrentLeagueKind { get; set; }

    public bool IsActive { get; set; }

    public int RoundWins { get; set; }

    public int StageWins { get; set; }

    public int StageSeconds { get; set; }

    public int StageThirds { get; set; }

    public int? BestSeasonFinish { get; set; }

    public int? BestSeasonNumber { get; set; }

    public int LifetimeEarnedBonusThousandths { get; set; }

    public int CurrentEffectiveBonusThousandths { get; set; }

    public int LastSeasonNumber { get; set; }

    public int LastStageNumber { get; set; }
}
