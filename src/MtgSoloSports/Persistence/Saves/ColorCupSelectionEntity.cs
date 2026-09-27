namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One persisted Color Cup team selection row per selected athlete per completed
/// source season. MSS-023 persists only the automatic team selection (no Cup
/// simulation yet): exactly four rows per sporting color (8 x 4 = 32), ordered
/// #1..#4 by final selection rating. Raw inputs plus normalized components plus
/// the final rating are stored so the user can understand why an athlete was
/// chosen without recomputing history. Rebuildable from <c>StageStandings</c> /
/// <c>SeasonStandings</c> / <c>AthleteCareers</c> / <c>Honours</c> /
/// <c>SeasonMemberships</c> plus the save rules snapshot.
/// Superleague athletes represent their original sporting color
/// (<c>SaveAthletes.SportingColor</c>); selection never reassigns color.
/// </summary>
public sealed class ColorCupSelectionEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int SportingColor { get; set; }

    public int SaveAthleteId { get; set; }

    public int SelectionRank { get; set; }

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
