using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;

/// <summary>
/// Structural invariants for normal automatic Superleague movement (Season 2+).
/// Fundamental failures throw and abort the mutation; corrupted sporting state
/// is never silently repaired. No Superleague color quota is enforced.
/// This slice never fills feeder vacancies from the common pool: the pool
/// athlete set must be identical before and after, and every next-season
/// feeder member must already have been active in the source season (either
/// retained in the same color or returning from the Superleague).
/// </summary>
public static class AutomaticMovementInvariants
{
    /// <summary>
    /// Validates the 48 picks before persistence: exactly 8 promotions, 8
    /// relegations, 8 qualifier incumbents and 24 challengers, all distinct
    /// athletes, correct rank bands, one champion plus three challengers per
    /// feeder and the Superleague 17-24 / 25-32 bands.
    /// </summary>
    public static void ValidateSelection(
        AutomaticMovementSelection.AutomaticPlan plan,
        IReadOnlyList<SeasonStandingEntity> superleagueStandings,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandingsByLeague,
        IReadOnlyList<LeagueEntity> feederLeagues,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(superleagueStandings);
        ArgumentNullException.ThrowIfNull(feederStandingsByLeague);
        ArgumentNullException.ThrowIfNull(feederLeagues);
        ArgumentNullException.ThrowIfNull(superleague);
        ArgumentNullException.ThrowIfNull(rules);

        if (plan.Promotions.Count != rules.FeederAutoPromotedCount)
        {
            throw new InvalidOperationException(
                $"Automatic movement must promote exactly {rules.FeederAutoPromotedCount} feeder champions, was {plan.Promotions.Count}.");
        }

        if (plan.Relegations.Count != rules.SuperleagueRelegatedCount)
        {
            throw new InvalidOperationException(
                $"Automatic movement must relegate exactly {rules.SuperleagueRelegatedCount} Superleague athletes, was {plan.Relegations.Count}.");
        }

        if (plan.QualifierIncumbents.Count != rules.SuperleagueQualifierIncumbentCount)
        {
            throw new InvalidOperationException(
                $"Automatic movement must mark exactly {rules.SuperleagueQualifierIncumbentCount} qualifier incumbents, was {plan.QualifierIncumbents.Count}.");
        }

        if (plan.QualifierChallengers.Count != rules.FeederQualifierCount)
        {
            throw new InvalidOperationException(
                $"Automatic movement must mark exactly {rules.FeederQualifierCount} qualifier challengers, was {plan.QualifierChallengers.Count}.");
        }

        if (plan.All.Count != rules.FeederAutoPromotedCount + rules.SuperleagueRelegatedCount + rules.SuperleagueQualifierIncumbentCount + rules.FeederQualifierCount)
        {
            throw new InvalidOperationException(
                $"Automatic movement must persist exactly 48 movement records, was {plan.All.Count}.");
        }

        HashSet<int> athleteIds = new();
        foreach (AutomaticMovementSelection.AutomaticPick pick in plan.All)
        {
            if (pick.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Automatic movement contains invalid athlete id {pick.SaveAthleteId}.");
            }

            if (!athleteIds.Add(pick.SaveAthleteId))
            {
                throw new InvalidOperationException($"Automatic movement contains duplicate athlete id {pick.SaveAthleteId}.");
            }
        }

        CheckSuperleagueBands(plan, superleague, rules);
        CheckFeederBands(plan, feederLeagues, rules);
        CheckRanksMatchStandings(plan, superleagueStandings, feederStandingsByLeague);
    }

    /// <summary>
    /// Validates persisted next-season state: consecutive season numbers with
    /// Superleague, nine leagues, 2048 memberships per season, no dual-active
    /// athlete, provisional 32-athlete Superleague (16 safe + 8 promoted + 8
    /// incumbents), pool set identical, 48 movements with exact kind counts and
    /// preserved sporting colors. Per-feeder counts may vary pending
    /// rebalancing; total feeder membership must be 256.
    /// </summary>
    public static void ValidateCreated(
        SeasonEntity sourceSeason,
        SeasonEntity nextSeason,
        LeagueEntity nextSuperleague,
        IReadOnlyList<LeagueEntity> nextFeeders,
        IReadOnlyList<SeasonMembershipEntity> sourceMemberships,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        IReadOnlyList<MovementEntity> movements,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(sourceSeason);
        ArgumentNullException.ThrowIfNull(nextSeason);
        ArgumentNullException.ThrowIfNull(nextSuperleague);
        ArgumentNullException.ThrowIfNull(nextFeeders);
        ArgumentNullException.ThrowIfNull(sourceMemberships);
        ArgumentNullException.ThrowIfNull(nextMemberships);
        ArgumentNullException.ThrowIfNull(movements);
        ArgumentNullException.ThrowIfNull(rules);

        if (!sourceSeason.HasSuperleague || !sourceSeason.IsComplete)
        {
            throw new InvalidOperationException("Automatic movement requires a completed source season with a Superleague.");
        }

        if (nextSeason.SeasonNumber != sourceSeason.SeasonNumber + 1 || !nextSeason.HasSuperleague || nextSeason.IsComplete)
        {
            throw new InvalidOperationException("Next season must be the consecutive incomplete Superleague season.");
        }

        if (nextSuperleague.SeasonId != nextSeason.Id || nextSuperleague.Kind != (int)LeagueKind.Superleague)
        {
            throw new InvalidOperationException("Next Superleague league row is corrupt.");
        }

        if (nextFeeders.Count != rules.TieredFeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Next season must have exactly {rules.TieredFeederLeagueCount} feeder leagues, was {nextFeeders.Count}.");
        }

        if (sourceMemberships.Count != rules.TotalAthletesInSave || nextMemberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Each season must carry exactly {rules.TotalAthletesInSave} memberships.");
        }

        CheckMembershipShape(nextMemberships, nextSuperleague, nextFeeders, rules);
        CheckPoolAndColors(sourceMemberships, nextMemberships);
        CheckMovements(movements, sourceSeason, nextSeason, nextSuperleague, nextFeeders, rules);
    }

    private static void CheckMembershipShape(
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        LeagueEntity nextSuperleague,
        IReadOnlyList<LeagueEntity> nextFeeders,
        RulesV1 rules)
    {
        CheckNoDualActive(nextMemberships);
        CheckSuperleagueProvisional(nextMemberships, nextSuperleague, rules);
        CheckFeederTotals(nextMemberships, nextFeeders, nextSuperleague, rules);
    }

    private static void CheckSuperleagueBands(
        AutomaticMovementSelection.AutomaticPlan plan,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        CheckIncumbentBands(plan, superleague, rules);
        CheckRelegationBands(plan, superleague, rules);
        CheckPromotionBands(plan);
    }

    private static void CheckIncumbentBands(
        AutomaticMovementSelection.AutomaticPlan plan,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        foreach (AutomaticMovementSelection.AutomaticPick pick in plan.QualifierIncumbents)
        {
            if (pick.FromLeagueId != superleague.Id)
            {
                throw new InvalidOperationException($"Qualifier incumbent {pick.SaveAthleteId} must come from the Superleague.");
            }

            int first = rules.SuperleagueSafeCount + 1;
            int last = rules.SuperleagueSafeCount + rules.SuperleagueQualifierIncumbentCount;
            if (pick.FromSeasonRank < first || pick.FromSeasonRank > last)
            {
                throw new InvalidOperationException(
                    $"Qualifier incumbent rank {pick.FromSeasonRank} is outside places {first}-{last}.");
            }

            if (pick.Kind != MovementKind.QualifierIncumbent)
            {
                throw new InvalidOperationException($"Qualifier incumbent {pick.SaveAthleteId} has corrupt kind {pick.Kind}.");
            }
        }
    }

    private static void CheckRelegationBands(
        AutomaticMovementSelection.AutomaticPlan plan,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        foreach (AutomaticMovementSelection.AutomaticPick pick in plan.Relegations)
        {
            if (pick.FromLeagueId != superleague.Id)
            {
                throw new InvalidOperationException($"Relegated athlete {pick.SaveAthleteId} must come from the Superleague.");
            }

            int first = rules.SuperleagueSafeCount + rules.SuperleagueQualifierIncumbentCount + 1;
            if (pick.FromSeasonRank < first || pick.FromSeasonRank > rules.SuperleagueSize)
            {
                throw new InvalidOperationException(
                    $"Relegated rank {pick.FromSeasonRank} is outside places {first}-{rules.SuperleagueSize}.");
            }

            if (pick.Kind != MovementKind.AutomaticRelegation)
            {
                throw new InvalidOperationException($"Relegated athlete {pick.SaveAthleteId} has corrupt kind {pick.Kind}.");
            }
        }
    }

    private static void CheckPromotionBands(AutomaticMovementSelection.AutomaticPlan plan)
    {
        foreach (AutomaticMovementSelection.AutomaticPick pick in plan.Promotions)
        {
            if (pick.FromSeasonRank != 1)
            {
                throw new InvalidOperationException(
                    $"Promoted athlete {pick.SaveAthleteId} must be a feeder champion (rank 1), was rank {pick.FromSeasonRank}.");
            }

            if (pick.Kind != MovementKind.AutomaticPromotion)
            {
                throw new InvalidOperationException($"Promoted athlete {pick.SaveAthleteId} has corrupt kind {pick.Kind}.");
            }
        }

        CheckChallengerBands(plan);
    }

    private static void CheckChallengerBands(AutomaticMovementSelection.AutomaticPlan plan)
    {
        foreach (AutomaticMovementSelection.AutomaticPick pick in plan.QualifierChallengers)
        {
            if (pick.FromSeasonRank is < 2 or > 4)
            {
                throw new InvalidOperationException(
                    $"Qualifier challenger rank {pick.FromSeasonRank} is outside places 2-4.");
            }

            if (pick.Kind != MovementKind.QualifierChallenger)
            {
                throw new InvalidOperationException($"Qualifier challenger {pick.SaveAthleteId} has corrupt kind {pick.Kind}.");
            }
        }
    }

    private static void CheckFeederBands(
        AutomaticMovementSelection.AutomaticPlan plan,
        IReadOnlyList<LeagueEntity> feederLeagues,
        RulesV1 rules)
    {
        IReadOnlyList<LeagueEntity> sources = ResolveExpectedSources(feederLeagues, rules);
        HashSet<int> feederIds = sources.Select(l => l.Id).ToHashSet();

        Dictionary<int, int> promotionsPerLeague = CountByLeague(plan.Promotions);
        Dictionary<int, int> challengersPerLeague = CountByLeague(plan.QualifierChallengers);

        foreach (LeagueEntity league in sources)
        {
            if (!promotionsPerLeague.TryGetValue(league.Id, out int promoted) || promoted != 1)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must contribute exactly one automatic promotion, was {promoted}.");
            }

            if (!challengersPerLeague.TryGetValue(league.Id, out int challengers) || challengers != 3)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must contribute exactly three qualifier challengers, was {challengers}.");
            }
        }

        foreach (AutomaticMovementSelection.AutomaticPick pick in plan.Promotions.Concat(plan.QualifierChallengers))
        {
            if (!feederIds.Contains(pick.FromLeagueId))
            {
                throw new InvalidOperationException($"Feeder pick {pick.SaveAthleteId} references unknown league {pick.FromLeagueId}.");
            }
        }
    }

    private static IReadOnlyList<LeagueEntity> ResolveExpectedSources(
        IReadOnlyList<LeagueEntity> feederLeagues,
        RulesV1 rules)
    {
        bool tiered = rules.FeederDivisionsPerColor == 3;
        if (!tiered)
        {
            return feederLeagues;
        }

        return feederLeagues
            .Where(l => l.FeederDivision == (int)SimulationKernel.Leagues.FeederDivision.First)
            .ToList();
    }

    private static Dictionary<int, int> CountByLeague(IReadOnlyList<AutomaticMovementSelection.AutomaticPick> picks)
    {
        return picks.GroupBy(p => p.FromLeagueId).ToDictionary(g => g.Key, g => g.Count());
    }

    private static void CheckRanksMatchStandings(
        AutomaticMovementSelection.AutomaticPlan plan,
        IReadOnlyList<SeasonStandingEntity> superleagueStandings,
        IReadOnlyDictionary<int, IReadOnlyList<SeasonStandingEntity>> feederStandingsByLeague)
    {
        Dictionary<int, int> superRankByAthlete = superleagueStandings.ToDictionary(r => r.SaveAthleteId, r => r.SeasonRank);
        Dictionary<int, int> feederRankByAthlete = new();
        foreach ((int _, IReadOnlyList<SeasonStandingEntity> rows) in feederStandingsByLeague)
        {
            foreach (SeasonStandingEntity row in rows)
            {
                feederRankByAthlete[row.SaveAthleteId] = row.SeasonRank;
            }
        }

        foreach (AutomaticMovementSelection.AutomaticPick pick in plan.All)
        {
            bool inSuper = superRankByAthlete.TryGetValue(pick.SaveAthleteId, out int superRank);
            bool inFeeder = feederRankByAthlete.TryGetValue(pick.SaveAthleteId, out int feederRank);
            if (inSuper == inFeeder)
            {
                throw new InvalidOperationException(
                    $"Athlete {pick.SaveAthleteId} must appear in exactly one source table.");
            }

            int actual = inSuper ? superRank : feederRank;
            if (actual != pick.FromSeasonRank)
            {
                throw new InvalidOperationException(
                    $"Athlete {pick.SaveAthleteId} rank {pick.FromSeasonRank} does not match final standing rank {actual}.");
            }
        }
    }

    private static void CheckPoolAndColors(
        IReadOnlyList<SeasonMembershipEntity> sourceMemberships,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships)
    {
        CheckPoolIdentical(sourceMemberships, nextMemberships);
        CheckColorsPreserved(sourceMemberships, nextMemberships);
    }

    private static void CheckNoDualActive(IReadOnlyList<SeasonMembershipEntity> nextMemberships)
    {
        HashSet<int> active = new();
        foreach (SeasonMembershipEntity membership in nextMemberships)
        {
            if (membership.LeagueId is null)
            {
                continue;
            }

            if (!active.Add(membership.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Athlete {membership.SaveAthleteId} is active in two leagues in Season {membership.SeasonId}.");
            }
        }
    }

    private static void CheckSuperleagueProvisional(
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        LeagueEntity nextSuperleague,
        RulesV1 rules)
    {
        int count = nextMemberships.Count(m => m.LeagueId == nextSuperleague.Id);
        if (count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Next Superleague must provisionally contain exactly {rules.SuperleagueSize} athletes (16 safe + 8 promoted + 8 incumbents), was {count}.");
        }
    }

    private static void CheckFeederTotals(
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        IReadOnlyList<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague,
        RulesV1 rules)
    {
        HashSet<int> feederIds = nextFeeders.Select(l => l.Id).ToHashSet();
        int feederTotal = nextMemberships.Count(m => m.LeagueId is not null && feederIds.Contains(m.LeagueId.Value));
        int expectedFeeders = rules.TieredFeederLeagueCount * rules.LeagueSize - rules.FeederAutoPromotedCount + rules.SuperleagueRelegatedCount;
        if (feederTotal != expectedFeeders)
        {
            throw new InvalidOperationException(
                $"Next feeder leagues must hold exactly {expectedFeeders} athletes pending rebalancing, was {feederTotal}.");
        }

        foreach (SeasonMembershipEntity membership in nextMemberships)
        {
            if (membership.LeagueId is null || membership.LeagueId == nextSuperleague.Id)
            {
                continue;
            }

            if (!feederIds.Contains(membership.LeagueId.Value))
            {
                throw new InvalidOperationException($"Membership {membership.Id} references unknown league {membership.LeagueId}.");
            }
        }
    }

    private static void CheckPoolIdentical(
        IReadOnlyList<SeasonMembershipEntity> sourceMemberships,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships)
    {
        HashSet<int> poolSource = sourceMemberships
            .Where(m => m.LeagueId is null)
            .Select(m => m.SaveAthleteId)
            .ToHashSet();
        HashSet<int> poolNext = nextMemberships
            .Where(m => m.LeagueId is null)
            .Select(m => m.SaveAthleteId)
            .ToHashSet();
        if (!poolSource.SetEquals(poolNext))
        {
            throw new InvalidOperationException(
                "Next-season pool must be identical to the source-season pool; feeder vacancies fill only in rebalancing.");
        }
    }

    private static void CheckColorsPreserved(
        IReadOnlyList<SeasonMembershipEntity> sourceMemberships,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships)
    {
        Dictionary<int, int> colorByAthlete = sourceMemberships.ToDictionary(m => m.SaveAthleteId, m => m.SportingColor);
        foreach (SeasonMembershipEntity membership in nextMemberships)
        {
            if (!colorByAthlete.TryGetValue(membership.SaveAthleteId, out int sourceColor))
            {
                throw new InvalidOperationException($"Next-season member {membership.SaveAthleteId} has no source membership.");
            }

            if (membership.SportingColor != sourceColor)
            {
                throw new InvalidOperationException(
                    $"Athlete {membership.SaveAthleteId} changed sporting color {sourceColor} to {membership.SportingColor}; returning athletes keep their color.");
            }
        }
    }

    private static void CheckMovements(
        IReadOnlyList<MovementEntity> movements,
        SeasonEntity sourceSeason,
        SeasonEntity nextSeason,
        LeagueEntity nextSuperleague,
        IReadOnlyList<LeagueEntity> nextFeeders,
        RulesV1 rules)
    {
        int expected = rules.FeederAutoPromotedCount + rules.SuperleagueRelegatedCount + rules.SuperleagueQualifierIncumbentCount + rules.FeederQualifierCount;
        if (movements.Count != expected)
        {
            throw new InvalidOperationException(
                $"Automatic movement must persist exactly {expected} records, was {movements.Count}.");
        }

        int promotions = movements.Count(m => m.Kind == (int)MovementKind.AutomaticPromotion);
        int relegations = movements.Count(m => m.Kind == (int)MovementKind.AutomaticRelegation);
        int incumbents = movements.Count(m => m.Kind == (int)MovementKind.QualifierIncumbent);
        int challengers = movements.Count(m => m.Kind == (int)MovementKind.QualifierChallenger);
        if (promotions != rules.FeederAutoPromotedCount)
        {
            throw new InvalidOperationException(
                $"Automatic movement must persist exactly {rules.FeederAutoPromotedCount} promotions, was {promotions}.");
        }

        if (relegations != rules.SuperleagueRelegatedCount)
        {
            throw new InvalidOperationException(
                $"Automatic movement must persist exactly {rules.SuperleagueRelegatedCount} relegations, was {relegations}.");
        }

        if (incumbents != rules.SuperleagueQualifierIncumbentCount)
        {
            throw new InvalidOperationException(
                $"Automatic movement must persist exactly {rules.SuperleagueQualifierIncumbentCount} qualifier incumbents, was {incumbents}.");
        }

        if (challengers != rules.FeederQualifierCount)
        {
            throw new InvalidOperationException(
                $"Automatic movement must persist exactly {rules.FeederQualifierCount} qualifier challengers, was {challengers}.");
        }

        CheckMovementLinkage(movements, sourceSeason, nextSeason);
        CheckMovementTargets(movements, nextSuperleague, nextFeeders);
    }

    private static void CheckMovementLinkage(
        IReadOnlyList<MovementEntity> movements,
        SeasonEntity sourceSeason,
        SeasonEntity nextSeason)
    {
        HashSet<int> athleteIds = new();
        foreach (MovementEntity movement in movements)
        {
            if (movement.FromSeasonId != sourceSeason.Id || movement.ToSeasonId != nextSeason.Id)
            {
                throw new InvalidOperationException($"Movement {movement.Id} has corrupt season linkage.");
            }

            if (!athleteIds.Add(movement.SaveAthleteId))
            {
                throw new InvalidOperationException($"Movement contains duplicate athlete id {movement.SaveAthleteId}.");
            }

            if (!Enum.IsDefined(typeof(MovementKind), movement.Kind))
            {
                throw new InvalidOperationException($"Movement {movement.Id} has unexpected kind {movement.Kind}.");
            }
        }
    }

    private static void CheckMovementTargets(
        IReadOnlyList<MovementEntity> movements,
        LeagueEntity nextSuperleague,
        IReadOnlyList<LeagueEntity> nextFeeders)
    {
        HashSet<int> feederIds = nextFeeders.Select(l => l.Id).ToHashSet();
        foreach (MovementEntity movement in movements)
        {
            CheckSingleMovementTarget(movement, nextSuperleague, feederIds);
        }
    }

    private static void CheckSingleMovementTarget(
        MovementEntity movement,
        LeagueEntity nextSuperleague,
        HashSet<int> feederIds)
    {
        switch ((MovementKind)movement.Kind)
        {
            case MovementKind.AutomaticPromotion:
                if (movement.ToLeagueId != nextSuperleague.Id)
                {
                    throw new InvalidOperationException($"Promotion {movement.Id} must target the next Superleague.");
                }

                if (movement.FromSeasonRank != 1)
                {
                    throw new InvalidOperationException($"Promotion {movement.Id} must carry source rank 1.");
                }

                break;
            case MovementKind.AutomaticRelegation:
                if (!feederIds.Contains(movement.ToLeagueId))
                {
                    throw new InvalidOperationException($"Relegation {movement.Id} must target a returning-color feeder.");
                }

                break;
            case MovementKind.QualifierIncumbent:
                if (movement.ToLeagueId != nextSuperleague.Id)
                {
                    throw new InvalidOperationException($"Qualifier incumbent {movement.Id} must provisionally remain in the Superleague.");
                }

                break;
            case MovementKind.QualifierChallenger:
                if (!feederIds.Contains(movement.ToLeagueId))
                {
                    throw new InvalidOperationException($"Qualifier challenger {movement.Id} must provisionally remain in its feeder.");
                }

                break;
            default:
                throw new InvalidOperationException($"Movement {movement.Id} has unexpected kind {movement.Kind}.");
        }
    }
}
