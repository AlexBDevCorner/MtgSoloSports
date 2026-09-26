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
    /// athletes, four per source league, ranks 1-4 only.
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

        Dictionary<int, int> perLeague = picks.GroupBy(p => p.FromLeagueId).ToDictionary(g => g.Key, g => g.Count());
        foreach (LeagueEntity league in feederLeagues)
        {
            if (!perLeague.TryGetValue(league.Id, out int count) || count != rules.InauguralQualifiedPerLeague)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must contribute exactly {rules.InauguralQualifiedPerLeague} athletes, was {count}.");
            }
        }
    }

    /// <summary>
    /// Validates persisted Season 2 state after the inaugural transition:
    /// one Superleague with exactly 32 members, eight feeders with 28 retained
    /// members each (vacancies left for the rebalancing flow), no athlete active
    /// in two leagues, and an untouched common-pool athlete set.
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

        if (seasonTwoFeeders.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season 2 must have exactly {rules.RegularLeagueCount} feeder leagues, was {seasonTwoFeeders.Count}.");
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
        Dictionary<int, LeagueEntity> feederByColor = seasonTwoFeeders.ToDictionary(l => l.SportingColor);

        foreach (LeagueEntity feeder in seasonTwoFeeders)
        {
            int retained = seasonTwoMemberships.Count(m => m.LeagueId == feeder.Id);
            int expected = rules.LeagueSize - rules.InauguralQualifiedPerLeague;
            if (retained != expected)
            {
                throw new InvalidOperationException(
                    $"League '{feeder.Name}' must retain exactly {expected} athletes pending rebalancing, was {retained}.");
            }
        }

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

            if (!feederByColor.TryGetValue(membership.SportingColor, out LeagueEntity? expectedFeeder)
                || expectedFeeder.Id != membership.LeagueId)
            {
                throw new InvalidOperationException(
                    $"Season 2 feeder member {membership.SaveAthleteId} is in the wrong color league.");
            }
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
