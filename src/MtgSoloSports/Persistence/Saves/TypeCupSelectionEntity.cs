namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One persisted Type Cup team allocation row per selected athlete per completed
/// even source season (MSS-026 persists only the allocation, no Cup simulation).
/// Each participating creature type fields exactly one team of four distinct
/// currently active athletes ordered #1..#4 by final selection rating; types that
/// cannot field four do not participate and there is no artificial team limit.
/// Raw inputs plus globally normalized components plus the final rating plus the
/// athlete's rank within its creature type are stored so the user can understand
/// why an athlete was chosen without recomputing history. Rebuildable from
/// <c>StageStandings</c> / <c>SeasonStandings</c> / <c>SeasonMemberships</c> /
/// <c>Honours</c> plus the save rules snapshot and <c>SaveAthletes</c> printed
/// types and permanent nationality. Persisting this allocation never sets
/// <c>SaveAthletes.TypeCupNationality</c>; nationality becomes permanent only
/// when the athlete actually participates (MSS-027).
/// All sporting values are fixed-point thousandths integers.
/// </summary>
public sealed class TypeCupSelectionEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public string CreatureType { get; set; } = string.Empty;

    public int SaveAthleteId { get; set; }

    public int SelectionRank { get; set; }

    public int TypeRank { get; set; }

    public int FinalRatingThousandths { get; set; }

    public int BonusNormThousandths { get; set; }

    public int PerformanceNormThousandths { get; set; }

    public int FormNormThousandths { get; set; }

    public int PrestigeNormThousandths { get; set; }

    public int BonusRawThousandths { get; set; }

    public int PerformanceRawThousandths { get; set; }

    public int FormRaw { get; set; }

    public int PrestigeRaw { get; set; }

    public int RulesVersion { get; set; }
}
