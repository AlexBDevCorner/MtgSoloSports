using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Leagues.InauguralDraw;

/// <summary>
/// Persisted Season 1 invariants. Split from draw invariants to keep methods small.
/// </summary>
public static class Season1PersistedInvariants
{
    /// <summary>
    /// Validates persisted Season 1 state: one season without Superleague, eight
    /// feeder leagues, 256 active memberships (32 per league, color-matched, in
    /// draw order) and 1,792 pool memberships (224 per color), with every save
    /// athlete present exactly once.
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
        EnsureLeagueCoverage(leagues);
        EnsureMembershipTotal(memberships, totalSaveAthletes, rules);
        PersistedCounts counts = AccumulateMemberships(leagues, memberships, rules);
        EnsurePersistedPerColor(counts, rules);
        EnsurePersistedActiveTotal(counts, rules);
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

        if (leagues.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season 1 must have exactly {rules.RegularLeagueCount} leagues, was {leagues.Count}.");
        }
    }

    private static void EnsureLeagueCoverage(IReadOnlyList<PersistedLeague> leagues)
    {
        HashSet<SportingColor> colors = [];
        foreach (PersistedLeague league in leagues)
        {
            if (league.Kind != 0)
            {
                throw new InvalidOperationException($"Season 1 league '{league.Name}' must be a feeder league.");
            }

            if (league.FeederDivision != (int)SimulationKernel.Leagues.FeederDivision.First)
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
        Dictionary<SportingColor, int> PoolPerColor);

    private static PersistedCounts AccumulateMemberships(
        IReadOnlyList<PersistedLeague> leagues,
        IReadOnlyList<PersistedMembership> memberships,
        RulesV1 rules)
    {
        Dictionary<int, PersistedLeague> byId = leagues.ToDictionary(l => l.LeagueId);
        HashSet<int> athleteIds = new();
        Dictionary<SportingColor, int> active = NewColorMap(static () => 0);
        Dictionary<SportingColor, int> pool = NewColorMap(static () => 0);

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
            }
        }

        return new PersistedCounts(active, pool);
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

        if (membership.LeagueId is null)
        {
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

        if (membership.DrawIndex >= rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Season 1 league member draw index must be below {rules.LeagueSize}, was {membership.DrawIndex}.");
        }
    }

    private static void EnsurePersistedPerColor(PersistedCounts counts, RulesV1 rules)
    {
        int expectedPool = rules.AthletesPerSportingColor - rules.LeagueSize;
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            if (counts.ActivePerColor[color] != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"Season 1 league {color} must contain exactly {rules.LeagueSize} athletes, was {counts.ActivePerColor[color]}.");
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
        int expected = rules.RegularLeagueCount * rules.LeagueSize;
        if (active != expected)
        {
            throw new InvalidOperationException(
                $"Season 1 must have exactly {expected} active athletes, was {active}.");
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
