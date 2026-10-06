using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Leagues.InauguralDraw;

/// <summary>
/// Persisted Season 1 invariants. Split from draw invariants to keep methods small.
/// Version-aware: v1 expects 8 feeder leagues (one F1 per color, 32 active /
/// 224 pool per color, 256 active globally); v2 tiered expects 24 feeder
/// leagues (F1/F2/F3 per color, 96 active / 160 pool per color, 768 active
/// globally) with divisions derived from draw index (0..31 F1, 32..63 F2,
/// 64..95 F3). Historical v1 feeders are never reinterpreted as tiered.
/// </summary>
public static class Season1PersistedInvariants
{
    /// <summary>
    /// Validates persisted Season 1 state: one season without Superleague, the
    /// versioned feeder-league count (8 for v1, 24 for v2), versioned active/pool
    /// memberships (256/1792 for v1, 768/1280 for v2, color-matched, in draw
    /// order) with every save athlete present exactly once and explicit feeder
    /// division per roster.
    /// </summary>
    public static void ValidatePersistedSeason1(
        int seasonNumber,
        bool hasSuperleague,
        IReadOnlyList<PersistedLeague> leagues,
        IReadOnlyList<PersistedMembership> memberships,
        int totalSaveAthletes,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(leagues);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(rules);
        EnsureSeasonHeader(seasonNumber, hasSuperleague, leagues, rules);
        EnsureLeagueCoverage(leagues, rules);
        EnsureMembershipTotal(memberships, totalSaveAthletes, rules);
        PersistedCounts counts = AccumulateMemberships(leagues, memberships, rules);
        EnsurePersistedPerColor(counts, rules);
        EnsurePersistedActiveTotal(counts, rules);
        EnsurePerLeagueCounts(leagues, counts, rules);
    }

    private static void EnsureSeasonHeader(
        int seasonNumber,
        bool hasSuperleague,
        IReadOnlyList<PersistedLeague> leagues,
        RulesV1 rules)
    {
        if (seasonNumber != 1)
        {
            throw new InvalidOperationException($"Season 1 must have season number 1, was {seasonNumber}.");
        }

        if (hasSuperleague)
        {
            throw new InvalidOperationException("Season 1 must have no Superleague.");
        }

        if (leagues.Count != rules.Season1FeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season 1 must have exactly {rules.Season1FeederLeagueCount} leagues, was {leagues.Count}.");
        }
    }

    private static void EnsureLeagueCoverage(IReadOnlyList<PersistedLeague> leagues, RulesV1 rules)
    {
        bool tiered = rules.FeederDivisionsPerColor == 3;
        if (!tiered)
        {
            EnsureSingleTierCoverage(leagues);
            return;
        }

        EnsureTieredCoverage(leagues, rules);
    }

    private static void EnsureSingleTierCoverage(IReadOnlyList<PersistedLeague> leagues)
    {
        HashSet<SportingColor> colors = [];
        foreach (PersistedLeague league in leagues)
        {
            if (league.Kind != 0)
            {
                throw new InvalidOperationException($"Season 1 league '{league.Name}' must be a feeder league.");
            }

            // Historical v1 rows predate the division column (stored None/0) or
            // carry explicit First (1) after migration; both surface as Feeder 1
            // without pretending F2/F3 existed.
            if (league.FeederDivision != (int)SimulationKernel.Leagues.FeederDivision.First &&
                league.FeederDivision != (int)SimulationKernel.Leagues.FeederDivision.None)
            {
                throw new InvalidOperationException($"Season 1 league '{league.Name}' must be Feeder 1, was division {league.FeederDivision}.");
            }

            if (!colors.Add(league.SportingColor))
            {
                throw new InvalidOperationException($"Season 1 has duplicate league for {league.SportingColor}.");
            }
        }

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            if (!colors.Contains(color))
            {
                throw new InvalidOperationException($"Season 1 is missing the {color} league.");
            }
        }
    }

    private static void EnsureTieredCoverage(IReadOnlyList<PersistedLeague> leagues, RulesV1 rules)
    {
        Dictionary<SportingColor, HashSet<int>> divisions = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            divisions[color] = [];
        }

        foreach (PersistedLeague league in leagues)
        {
            if (league.Kind != 0)
            {
                throw new InvalidOperationException($"Season 1 league '{league.Name}' must be a feeder league.");
            }

            if (league.FeederDivision != (int)SimulationKernel.Leagues.FeederDivision.First &&
                league.FeederDivision != (int)SimulationKernel.Leagues.FeederDivision.Second &&
                league.FeederDivision != (int)SimulationKernel.Leagues.FeederDivision.Third)
            {
                throw new InvalidOperationException($"Season 1 league '{league.Name}' must carry an explicit feeder division (1..3), was {league.FeederDivision}.");
            }

            if (!divisions[league.SportingColor].Add(league.FeederDivision))
            {
                throw new InvalidOperationException($"Season 1 has duplicate division {league.FeederDivision} for {league.SportingColor}.");
            }
        }

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            HashSet<int> seen = divisions[color];
            if (seen.Count != rules.FeederDivisionsPerColor)
            {
                throw new InvalidOperationException($"Season 1 is missing feeder divisions for {color} (has {seen.Count}, needs {rules.FeederDivisionsPerColor}).");
            }
        }
    }

    private static void EnsureMembershipTotal(
        IReadOnlyList<PersistedMembership> memberships,
        int totalSaveAthletes,
        RulesV1 rules)
    {
        if (memberships.Count != totalSaveAthletes || memberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Season 1 must have exactly {rules.TotalAthletesInSave} memberships, was {memberships.Count}.");
        }
    }

    private sealed record PersistedCounts(
        Dictionary<SportingColor, int> ActivePerColor,
        Dictionary<SportingColor, int> PoolPerColor,
        Dictionary<int, int> ActivePerLeague);

    private static PersistedCounts AccumulateMemberships(
        IReadOnlyList<PersistedLeague> leagues,
        IReadOnlyList<PersistedMembership> memberships,
        RulesV1 rules)
    {
        Dictionary<int, PersistedLeague> byId = leagues.ToDictionary(l => l.LeagueId);
        HashSet<int> athleteIds = new();
        Dictionary<SportingColor, int> active = NewColorMap(static () => 0);
        Dictionary<SportingColor, int> pool = NewColorMap(static () => 0);
        Dictionary<int, int> perLeague = leagues.ToDictionary(l => l.LeagueId, _ => 0);

        foreach (PersistedMembership membership in memberships)
        {
            CheckMembership(membership, byId, athleteIds, rules);
            if (membership.LeagueId is null)
            {
                pool[membership.SportingColor]++;
            }
            else
            {
                active[membership.SportingColor]++;
                perLeague[membership.LeagueId.Value]++;
            }
        }

        return new PersistedCounts(active, pool, perLeague);
    }

    private static void CheckMembership(
        PersistedMembership membership,
        Dictionary<int, PersistedLeague> byId,
        HashSet<int> athleteIds,
        RulesV1 rules)
    {
        if (!athleteIds.Add(membership.SaveAthleteId))
        {
            throw new InvalidOperationException(
                $"Season 1 contains duplicate membership for athlete id {membership.SaveAthleteId}.");
        }

        if (membership.DrawIndex < 0 || membership.DrawIndex >= rules.AthletesPerSportingColor)
        {
            throw new InvalidOperationException(
                $"Season 1 draw index {membership.DrawIndex} for athlete id {membership.SaveAthleteId} is out of range.");
        }

        bool tiered = rules.FeederDivisionsPerColor == 3;
        if (membership.LeagueId is null)
        {
            bool expectedPool = tiered
                ? membership.DrawIndex >= rules.Season1ActivePerColor
                : membership.DrawIndex >= rules.LeagueSize;
            if (!expectedPool)
            {
                throw new InvalidOperationException(
                    $"Season 1 pool member draw index must be at or above {rules.Season1ActivePerColor}, was {membership.DrawIndex}.");
            }

            return;
        }

        if (!byId.TryGetValue(membership.LeagueId.Value, out PersistedLeague? league))
        {
            throw new InvalidOperationException(
                $"Season 1 membership references unknown league {membership.LeagueId.Value}.");
        }

        if (league.SportingColor != membership.SportingColor)
        {
            throw new InvalidOperationException(
                $"Season 1 membership color {membership.SportingColor} does not match league {league.SportingColor}.");
        }

        if (!tiered)
        {
            if (membership.DrawIndex >= rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"Season 1 league member draw index must be below {rules.LeagueSize}, was {membership.DrawIndex}.");
            }

            return;
        }

        int expectedDivision = DivisionForDrawIndex(membership.DrawIndex, rules);
        if (league.FeederDivision != expectedDivision)
        {
            throw new InvalidOperationException(
                $"Season 1 member draw index {membership.DrawIndex} belongs to division {expectedDivision}, not league '{league.Name}' division {league.FeederDivision}.");
        }
    }

    internal static int DivisionForDrawIndex(int drawIndex, RulesV1 rules)
    {
        if (drawIndex < 0 || drawIndex >= rules.AthletesPerSportingColor)
        {
            throw new InvalidOperationException($"Draw index {drawIndex} is out of range.");
        }

        if (drawIndex < rules.LeagueSize)
        {
            return (int)SimulationKernel.Leagues.FeederDivision.First;
        }

        if (drawIndex < 2 * rules.LeagueSize)
        {
            return (int)SimulationKernel.Leagues.FeederDivision.Second;
        }

        if (drawIndex < 3 * rules.LeagueSize)
        {
            return (int)SimulationKernel.Leagues.FeederDivision.Third;
        }

        throw new InvalidOperationException($"Draw index {drawIndex} is a pool index, not a feeder division.");
    }

    private static void EnsurePersistedPerColor(PersistedCounts counts, RulesV1 rules)
    {
        int expectedActive = rules.Season1ActivePerColor;
        int expectedPool = rules.Season1PoolPerColor;
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            if (counts.ActivePerColor[color] != expectedActive)
            {
                throw new InvalidOperationException(
                    $"Season 1 league {color} must contain exactly {expectedActive} athletes, was {counts.ActivePerColor[color]}.");
            }

            if (counts.PoolPerColor[color] != expectedPool)
            {
                throw new InvalidOperationException(
                    $"Season 1 pool {color} must contain exactly {expectedPool} athletes, was {counts.PoolPerColor[color]}.");
            }
        }
    }

    private static void EnsurePersistedActiveTotal(PersistedCounts counts, RulesV1 rules)
    {
        int active = counts.ActivePerColor.Values.Sum();
        int expected = rules.Season1ActiveTotal;
        if (active != expected)
        {
            throw new InvalidOperationException(
                $"Season 1 must have exactly {expected} active athletes, was {active}.");
        }
    }

    private static void EnsurePerLeagueCounts(
        IReadOnlyList<PersistedLeague> leagues,
        PersistedCounts counts,
        RulesV1 rules)
    {
        foreach (PersistedLeague league in leagues)
        {
            if (!counts.ActivePerLeague.TryGetValue(league.LeagueId, out int count) || count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"Season 1 league '{league.Name}' must contain exactly {rules.LeagueSize} athletes, was {count}.");
            }
        }
    }

    private static Dictionary<SportingColor, int> NewColorMap(Func<int> factory)
    {
        Dictionary<SportingColor, int> map = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            map[color] = factory();
        }

        return map;
    }
}
