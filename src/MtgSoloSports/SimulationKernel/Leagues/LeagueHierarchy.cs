namespace MtgSoloSports.SimulationKernel.Leagues;

/// <summary>
/// Canonical hierarchy behavior for the four-level pyramid.
/// Single place for level order, feeder checks, adjacent tiers, display names,
/// and the default tier bonus scales (Superleague 2/1, Feeder 1 1/1,
/// Feeder 2 1/2, Feeder 3 1/4). Sporting math reads the authoritative scale
/// from the versioned rules snapshot; these defaults pin the canonical values
/// and keep feature code free of repeated <c>if Feeder2 ... else if Feeder3</c>.
/// Pure: no HTTP, EF Core, filesystem, clock, or network dependencies.
/// </summary>
public static class LeagueHierarchy
{
    public static readonly TierBonusScale SuperleagueScale = new(2, 1);

    public static readonly TierBonusScale Feeder1Scale = new(1, 1);

    public static readonly TierBonusScale Feeder2Scale = new(1, 2);

    public static readonly TierBonusScale Feeder3Scale = new(1, 4);

    public static IReadOnlyList<LeagueLevel> AllLevels { get; } =
        [LeagueLevel.Superleague, LeagueLevel.Feeder1, LeagueLevel.Feeder2, LeagueLevel.Feeder3];

    public static IReadOnlyList<LeagueLevel> FeederLevels { get; } =
        [LeagueLevel.Feeder1, LeagueLevel.Feeder2, LeagueLevel.Feeder3];

    public static bool IsSuperleague(LeagueLevel level) => level == LeagueLevel.Superleague;

    public static bool IsFeeder(LeagueLevel level) => level is LeagueLevel.Feeder1 or LeagueLevel.Feeder2 or LeagueLevel.Feeder3;

    /// <summary>
    /// Pyramid order: 0 is the top (Superleague), 3 is the bottom (Feeder 3).
    /// </summary>
    public static int Order(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => 0,
        LeagueLevel.Feeder1 => 1,
        LeagueLevel.Feeder2 => 2,
        LeagueLevel.Feeder3 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };

    public static FeederDivision FeederDivisionFor(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => FeederDivision.None,
        LeagueLevel.Feeder1 => FeederDivision.First,
        LeagueLevel.Feeder2 => FeederDivision.Second,
        LeagueLevel.Feeder3 => FeederDivision.Third,
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };

    public static LeagueLevel LevelForDivision(FeederDivision division, bool isSuperleague)
    {
        if (isSuperleague)
        {
            if (division != FeederDivision.None)
            {
                throw new InvalidOperationException($"Superleague must carry {nameof(FeederDivision)}.{nameof(FeederDivision.None)}, was {division}.");
            }

            return LeagueLevel.Superleague;
        }

        return division switch
        {
            FeederDivision.First => LeagueLevel.Feeder1,
            FeederDivision.Second => LeagueLevel.Feeder2,
            FeederDivision.Third => LeagueLevel.Feeder3,
            // Historical v1 feeder rows predate the division column (stored 0).
            // They represent the single v1 feeder tier, surfaced as Feeder 1
            // for display/compatibility without pretending F2/F3 existed.
            FeederDivision.None => LeagueLevel.Feeder1,
            _ => throw new ArgumentOutOfRangeException(nameof(division), $"Unknown feeder division {(int)division}."),
        };
    }

    public static LeagueLevel FromLegacySuperleagueFlag(bool isSuperleague) =>
        isSuperleague ? LeagueLevel.Superleague : LeagueLevel.Feeder1;

    /// <summary>
    /// Canonical default bonus scale for a tier. Versioned snapshots remain the
    /// authoritative source for sporting math; this helper pins the required
    /// 2/1, 1/1, 1/2, 1/4 values in one place.
    /// </summary>
    public static TierBonusScale DefaultBonusScale(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => SuperleagueScale,
        LeagueLevel.Feeder1 => Feeder1Scale,
        LeagueLevel.Feeder2 => Feeder2Scale,
        LeagueLevel.Feeder3 => Feeder3Scale,
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };

    /// <summary>
    /// Display name without parsing league names: "Superleague", "Feeder 1", etc.
    /// </summary>
    public static string DisplayName(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => "Superleague",
        LeagueLevel.Feeder1 => "Feeder 1",
        LeagueLevel.Feeder2 => "Feeder 2",
        LeagueLevel.Feeder3 => "Feeder 3",
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };

    /// <summary>
    /// The next tier toward the top of the pyramid, or null at the top.
    /// </summary>
    public static LeagueLevel? HigherTier(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => null,
        LeagueLevel.Feeder1 => LeagueLevel.Superleague,
        LeagueLevel.Feeder2 => LeagueLevel.Feeder1,
        LeagueLevel.Feeder3 => LeagueLevel.Feeder2,
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };

    /// <summary>
    /// The next tier toward the bottom of the pyramid, or null at the bottom.
    /// </summary>
    public static LeagueLevel? LowerTier(LeagueLevel level) => level switch
    {
        LeagueLevel.Superleague => LeagueLevel.Feeder1,
        LeagueLevel.Feeder1 => LeagueLevel.Feeder2,
        LeagueLevel.Feeder2 => LeagueLevel.Feeder3,
        LeagueLevel.Feeder3 => null,
        _ => throw new ArgumentOutOfRangeException(nameof(level), $"Unknown league level {(int)level}."),
    };
}
