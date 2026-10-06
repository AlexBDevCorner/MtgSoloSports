using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.CreateInaugural;

/// <summary>
/// Structural invariants for the Season 1 inaugural Superleague transition.
/// Fundamental failures throw and abort the mutation; corrupted sporting state
/// is never silently repaired. This slice never fills feeder vacancies from
/// the common pool: every Season 2 feeder member must already have been active
/// in the same feeder league in Season 1, and the pool athlete set must be
/// identical before and after.
/// </summary>
public static class InauguralSuperleagueInvariants
{
    /// <summary>
    /// Validates the 32 picked athletes before persistence: exactly 32 unique
    /// athletes, four per F1 source league, ranks 1-4 only. Tiered saves supply
    /// 24 feeders but only the 8 F1 leagues contribute; F2/F3 never skip tiers.
    /// </summary>
    public static void ValidateSelection(
        IReadOnlyList<InauguralSuperleagueSelection.InauguralPick> picks,
        IReadOnlyList<LeagueEntity> feederLeagues,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(picks);
        ArgumentNullException.ThrowIfNull(feederLeagues);
        ArgumentNullException.ThrowIfNull(rules);

        if (picks.Count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Inaugural Superleague must select exactly {rules.SuperleagueSize} athletes, was {picks.Count}.");
        }

        HashSet<int> athleteIds = new();
        foreach (InauguralSuperleagueSelection.InauguralPick pick in picks)
        {
            if (pick.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Inaugural selection contains invalid athlete id {pick.SaveAthleteId}.");
            }

            if (!athleteIds.Add(pick.SaveAthleteId))
            {
                throw new InvalidOperationException($"Inaugural selection contains duplicate athlete id {pick.SaveAthleteId}.");
            }

            if (pick.FromSeasonRank < 1 || pick.FromSeasonRank > rules.InauguralQualifiedPerLeague)
            {
                throw new InvalidOperationException(
                    $"Inaugural selection rank {pick.FromSeasonRank} is outside places 1-{rules.InauguralQualifiedPerLeague}.");
            }
        }

        List<LeagueEntity> sources = InauguralSuperleagueSelection.ResolveSources(feederLeagues, rules);
        Dictionary<int, int> perLeague = picks.GroupBy(p => p.FromLeagueId).ToDictionary(g => g.Key, g => g.Count());
        foreach (LeagueEntity league in sources)
        {
            if (!perLeague.TryGetValue(league.Id, out int count) || count != rules.InauguralQualifiedPerLeague)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must contribute exactly {rules.InauguralQualifiedPerLeague} athletes, was {count}.");
            }
        }

        if (perLeague.Count != sources.Count)
        {
            throw new InvalidOperationException(
                $"Inaugural selection must draw from exactly {sources.Count} F1 leagues, was {perLeague.Count}.");
        }
    }

    /// <summary>
    /// Validates persisted Season 2 state after the inaugural transition:
    /// one Superleague with exactly 32 members, versioned feeders (8 with 28
    /// retained each for v1; 24 with F1 at 28 and F2/F3 at 32 each for v2 tiered,
    /// vacancies left for the rebalancing flow), no athlete active in two
    /// leagues, and an untouched common-pool athlete set.
    /// </summary>
    public static void ValidateCreated(
        SeasonEntity seasonOne,
        SeasonEntity seasonTwo,
        LeagueEntity superleague,
        IReadOnlyList<LeagueEntity> seasonTwoFeeders,
        IReadOnlyList<SeasonMembershipEntity> seasonOneMemberships,
        IReadOnlyList<SeasonMembershipEntity> seasonTwoMemberships,
        IReadOnlyList<MovementEntity> movements,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(seasonOne);
        ArgumentNullException.ThrowIfNull(seasonTwo);
        ArgumentNullException.ThrowIfNull(superleague);
        ArgumentNullException.ThrowIfNull(seasonTwoFeeders);
        ArgumentNullException.ThrowIfNull(seasonOneMemberships);
        ArgumentNullException.ThrowIfNull(seasonTwoMemberships);
        ArgumentNullException.ThrowIfNull(movements);
        ArgumentNullException.ThrowIfNull(rules);

        if (seasonTwo.SeasonNumber != 2 || !seasonTwo.HasSuperleague || seasonTwo.IsComplete)
        {
            throw new InvalidOperationException("Season 2 must exist with HasSuperleague and incomplete.");
        }

        if (superleague.SeasonId != seasonTwo.Id || superleague.Kind != (int)LeagueKind.Superleague)
        {
            throw new InvalidOperationException("Inaugural Superleague league row is corrupt.");
        }

        if (seasonTwoFeeders.Count != rules.Season1FeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season 2 must have exactly {rules.Season1FeederLeagueCount} feeder leagues, was {seasonTwoFeeders.Count}.");
        }

        if (seasonOneMemberships.Count != rules.TotalAthletesInSave || seasonTwoMemberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Each season must carry exactly {rules.TotalAthletesInSave} memberships.");
        }

        CheckNoDualActive(seasonTwoMemberships);
        CheckSuperleagueCount(seasonTwoMemberships, superleague, rules);
        CheckFeederRetention(seasonOneMemberships, seasonTwoMemberships, seasonTwoFeeders, superleague, rules);
        CheckPoolUntouched(seasonOneMemberships, seasonTwoMemberships);
        CheckMovements(movements, seasonOne, seasonTwo, superleague, rules);
    }

    private static void CheckNoDualActive(IReadOnlyList<SeasonMembershipEntity> seasonTwoMemberships)
    {
        HashSet<int> active = new();
        foreach (SeasonMembershipEntity membership in seasonTwoMemberships)
        {
            if (membership.LeagueId is null)
            {
                continue;
            }

            if (!active.Add(membership.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Athlete {membership.SaveAthleteId} is active in two leagues in Season 2.");
            }
        }
    }

    private static void CheckSuperleagueCount(
        IReadOnlyList<SeasonMembershipEntity> seasonTwoMemberships,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        int count = seasonTwoMemberships.Count(m => m.LeagueId == superleague.Id);
        if (count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Superleague must contain exactly {rules.SuperleagueSize} athletes, was {count}.");
        }
    }

    private static void CheckFeederRetention(
        IReadOnlyList<SeasonMembershipEntity> seasonOneMemberships,
        IReadOnlyList<SeasonMembershipEntity> seasonTwoMemberships,
        IReadOnlyList<LeagueEntity> seasonTwoFeeders,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        HashSet<int> promoted = seasonTwoMemberships
            .Where(m => m.LeagueId == superleague.Id)
            .Select(m => m.SaveAthleteId)
            .ToHashSet();
        Dictionary<int, int> seasonOneLeagueByAthlete = seasonOneMemberships
            .Where(m => m.LeagueId is not null)
            .ToDictionary(m => m.SaveAthleteId, m => m.LeagueId!.Value);
        Dictionary<(int Color, int Division), LeagueEntity> feederByColorDivision = seasonTwoFeeders
            .ToDictionary(l => (l.SportingColor, l.FeederDivision));
        bool tiered = rules.FeederDivisionsPerColor == 3;

        CheckFeederCounts(seasonTwoMemberships, seasonTwoFeeders, rules, tiered);
        CheckFeederMembers(
            seasonOneMemberships, seasonTwoMemberships, seasonTwoFeeders,
            superleague, promoted, seasonOneLeagueByAthlete, feederByColorDivision, rules, tiered);
    }

    private static void CheckFeederCounts(
        IReadOnlyList<SeasonMembershipEntity> seasonTwoMemberships,
        IReadOnlyList<LeagueEntity> seasonTwoFeeders,
        RulesV1 rules,
        bool tiered)
    {
        foreach (LeagueEntity feeder in seasonTwoFeeders)
        {
            int retained = seasonTwoMemberships.Count(m => m.LeagueId == feeder.Id);
            int expected = ExpectedRetention(feeder, rules, tiered);
            if (retained != expected)
            {
                throw new InvalidOperationException(
                    $"League '{feeder.Name}' must retain exactly {expected} athletes pending rebalancing, was {retained}.");
            }
        }
    }

    private static int ExpectedRetention(LeagueEntity feeder, RulesV1 rules, bool tiered)
    {
        if (!tiered)
        {
            return rules.LeagueSize - rules.InauguralQualifiedPerLeague;
        }

        return feeder.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First
            ? rules.LeagueSize - rules.InauguralQualifiedPerLeague
            : rules.LeagueSize;
    }

    private static void CheckFeederMembers(
        IReadOnlyList<SeasonMembershipEntity> seasonOneMemberships,
        IReadOnlyList<SeasonMembershipEntity> seasonTwoMemberships,
        IReadOnlyList<LeagueEntity> seasonTwoFeeders,
        LeagueEntity superleague,
        HashSet<int> promoted,
        Dictionary<int, int> seasonOneLeagueByAthlete,
        Dictionary<(int Color, int Division), LeagueEntity> feederByColorDivision,
        RulesV1 rules,
        bool tiered)
    {
        foreach (SeasonMembershipEntity membership in seasonTwoMemberships)
        {
            if (membership.LeagueId is null || membership.LeagueId == superleague.Id)
            {
                continue;
            }

            if (promoted.Contains(membership.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Athlete {membership.SaveAthleteId} is active in two leagues in Season 2.");
            }

            if (!seasonOneLeagueByAthlete.ContainsKey(membership.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Season 2 feeder member {membership.SaveAthleteId} was not active in Season 1; pool fills are reserved for rebalancing.");
            }

            if (!tiered)
            {
                CheckSingleTierMember(membership, seasonTwoFeeders);
                continue;
            }

            CheckTieredMember(membership, seasonOneMemberships, feederByColorDivision, rules);
        }
    }

    private static void CheckSingleTierMember(
        SeasonMembershipEntity membership,
        IReadOnlyList<LeagueEntity> seasonTwoFeeders)
    {
        LeagueEntity? expectedSingle = seasonTwoFeeders.SingleOrDefault(l => l.SportingColor == membership.SportingColor);
        if (expectedSingle is null || expectedSingle.Id != membership.LeagueId)
        {
            throw new InvalidOperationException(
                $"Season 2 feeder member {membership.SaveAthleteId} is in the wrong color league.");
        }
    }

    private static void CheckTieredMember(
        SeasonMembershipEntity membership,
        IReadOnlyList<SeasonMembershipEntity> seasonOneMemberships,
        Dictionary<(int Color, int Division), LeagueEntity> feederByColorDivision,
        RulesV1 rules)
    {
        SeasonMembershipEntity? source = seasonOneMemberships.SingleOrDefault(m => m.SaveAthleteId == membership.SaveAthleteId);
        if (source?.LeagueId is null)
        {
            throw new InvalidOperationException(
                $"Season 2 feeder member {membership.SaveAthleteId} was not active in Season 1; pool fills are reserved for rebalancing.");
        }

        // Tier is preserved across the inaugural transition: F1 minus
        // promoted stays F1, F2 stays F2, F3 stays F3. No tier skipping.
        int sourceDivision = source.DrawIndex < rules.LeagueSize
            ? (int)SimulationKernel.Leagues.FeederDivision.First
            : source.DrawIndex < 2 * rules.LeagueSize
                ? (int)SimulationKernel.Leagues.FeederDivision.Second
                : (int)SimulationKernel.Leagues.FeederDivision.Third;
        if (!feederByColorDivision.TryGetValue((membership.SportingColor, sourceDivision), out LeagueEntity? expectedFeeder)
            || expectedFeeder.Id != membership.LeagueId)
        {
            throw new InvalidOperationException(
                $"Season 2 feeder member {membership.SaveAthleteId} is in the wrong color/division league.");
        }
    }

    private static void CheckPoolUntouched(
        IReadOnlyList<SeasonMembershipEntity> seasonOneMemberships,
        IReadOnlyList<SeasonMembershipEntity> seasonTwoMemberships)
    {
        HashSet<int> poolOne = seasonOneMemberships
            .Where(m => m.LeagueId is null)
            .Select(m => m.SaveAthleteId)
            .ToHashSet();
        HashSet<int> poolTwo = seasonTwoMemberships
            .Where(m => m.LeagueId is null)
            .Select(m => m.SaveAthleteId)
            .ToHashSet();
        if (!poolOne.SetEquals(poolTwo))
        {
            throw new InvalidOperationException(
                "Season 2 pool must be identical to Season 1 pool; feeder vacancies fill only in rebalancing.");
        }
    }

    private static void CheckMovements(
        IReadOnlyList<MovementEntity> movements,
        SeasonEntity seasonOne,
        SeasonEntity seasonTwo,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        if (movements.Count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Inaugural movement must persist exactly {rules.SuperleagueSize} records, was {movements.Count}.");
        }

        HashSet<int> athleteIds = new();
        foreach (MovementEntity movement in movements)
        {
            if (movement.Kind != (int)MovementKind.InauguralPromotion)
            {
                throw new InvalidOperationException($"Movement {movement.Id} has unexpected kind {movement.Kind}.");
            }

            if (movement.FromSeasonId != seasonOne.Id || movement.ToSeasonId != seasonTwo.Id)
            {
                throw new InvalidOperationException($"Movement {movement.Id} has corrupt season linkage.");
            }

            if (movement.ToLeagueId != superleague.Id)
            {
                throw new InvalidOperationException($"Movement {movement.Id} must target the Superleague.");
            }

            if (movement.FromSeasonRank < 1 || movement.FromSeasonRank > rules.InauguralQualifiedPerLeague)
            {
                throw new InvalidOperationException($"Movement {movement.Id} has corrupt source rank {movement.FromSeasonRank}.");
            }

            if (!athleteIds.Add(movement.SaveAthleteId))
            {
                throw new InvalidOperationException($"Movement contains duplicate athlete id {movement.SaveAthleteId}.");
            }
        }
    }
}
