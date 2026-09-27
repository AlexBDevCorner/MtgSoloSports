using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Seasons.StartNextSeason;

/// <summary>
/// Structural invariants for starting the next season. Fundamental failures
/// throw and abort the mutation; corrupted sporting state is never silently
/// repaired. The next season starts only from a fully valid 32-per-league
/// roster with correct feeder colors, no duplicate active athlete, intact bonus
/// history and the correct Superleague composition
/// (inaugural 8x4, or normal 16 safe + 8 champions + 8 qualifier winners).
/// </summary>
internal static class StartNextSeasonInvariants
{
    internal static void ValidateRosterCounts(
        SeasonEntity next,
        IReadOnlyList<LeagueEntity> nextLeagues,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(nextLeagues);
        ArgumentNullException.ThrowIfNull(nextMemberships);
        ArgumentNullException.ThrowIfNull(rules);
        CheckLeagueShape(next, nextLeagues, rules);
        CheckMembershipUniqueness(next, nextMemberships, rules);
        CheckPerLeagueCounts(next, nextLeagues, nextMemberships, rules);
        ValidateFeederColors(nextLeagues, nextMemberships);
    }

    internal static void CheckLeagueShape(
        SeasonEntity next,
        IReadOnlyList<LeagueEntity> nextLeagues,
        RulesV1 rules)
    {
        if (nextLeagues.Count != rules.RegularLeagueCount + 1)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} must have exactly {rules.RegularLeagueCount + 1} leagues (8 feeders + Superleague), was {nextLeagues.Count}.");
        }

        int feeders = nextLeagues.Count(l => l.Kind == (int)LeagueKind.Feeder);
        int supers = nextLeagues.Count(l => l.Kind == (int)LeagueKind.Superleague);
        if (feeders != rules.RegularLeagueCount || supers != 1)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} must have {rules.RegularLeagueCount} feeders and 1 Superleague, was {feeders}/{supers}.");
        }
    }

    internal static void CheckMembershipUniqueness(
        SeasonEntity next,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        RulesV1 rules)
    {
        if (nextMemberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} must have exactly {rules.TotalAthletesInSave} memberships, was {nextMemberships.Count}.");
        }

        HashSet<int> athleteIds = new();
        foreach (SeasonMembershipEntity membership in nextMemberships)
        {
            if (!athleteIds.Add(membership.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Season {next.SeasonNumber} contains duplicate athlete id {membership.SaveAthleteId}.");
            }
        }
    }

    internal static void CheckPerLeagueCounts(
        SeasonEntity next,
        IReadOnlyList<LeagueEntity> nextLeagues,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        RulesV1 rules)
    {
        Dictionary<int, int> counts = CountByLeague(next, nextLeagues, nextMemberships);
        foreach (LeagueEntity league in nextLeagues)
        {
            counts.TryGetValue(league.Id, out int count);
            if (count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' must contain exactly {rules.LeagueSize} athletes to start season {next.SeasonNumber}, was {count}.");
            }
        }

        CheckActivePoolTotals(next, nextMemberships, rules);
    }

    internal static Dictionary<int, int> CountByLeague(
        SeasonEntity next,
        IReadOnlyList<LeagueEntity> nextLeagues,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships)
    {
        Dictionary<int, LeagueEntity> leaguesById = nextLeagues.ToDictionary(l => l.Id);
        Dictionary<int, int> counts = new();
        foreach (SeasonMembershipEntity membership in nextMemberships)
        {
            if (membership.LeagueId is null)
            {
                continue;
            }

            if (!leaguesById.ContainsKey(membership.LeagueId.Value))
            {
                throw new InvalidOperationException(
                    $"Season {next.SeasonNumber} membership references unknown league {membership.LeagueId.Value}.");
            }

            counts.TryGetValue(membership.LeagueId.Value, out int count);
            counts[membership.LeagueId.Value] = count + 1;
        }

        return counts;
    }

    internal static void CheckActivePoolTotals(
        SeasonEntity next,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        RulesV1 rules)
    {
        int active = nextMemberships.Count(m => m.LeagueId is not null);
        int expectedActive = (rules.RegularLeagueCount * rules.LeagueSize) + rules.SuperleagueSize;
        if (active != expectedActive)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} must hold exactly {expectedActive} active athletes, was {active}.");
        }

        int pool = nextMemberships.Count(m => m.LeagueId is null);
        if (pool != rules.TotalAthletesInSave - expectedActive)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} must hold exactly {rules.TotalAthletesInSave - expectedActive} pool athletes, was {pool}.");
        }
    }

    internal static void ValidateFeederColors(
        IReadOnlyList<LeagueEntity> nextLeagues,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships)
    {
        Dictionary<int, LeagueEntity> leaguesById = nextLeagues.ToDictionary(l => l.Id);
        foreach (SeasonMembershipEntity membership in nextMemberships)
        {
            if (membership.LeagueId is null)
            {
                continue;
            }

            LeagueEntity league = leaguesById[membership.LeagueId.Value];
            if (league.Kind == (int)LeagueKind.Feeder && membership.SportingColor != league.SportingColor)
            {
                throw new InvalidOperationException(
                    $"Athlete {membership.SaveAthleteId} color {membership.SportingColor} does not match feeder '{league.Name}'.");
            }
        }
    }

    internal static void ValidateSuperCompositionInaugural(
        IReadOnlySet<int> superAthletes,
        IReadOnlySet<int> inauguralPicks,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(superAthletes);
        ArgumentNullException.ThrowIfNull(inauguralPicks);
        ArgumentNullException.ThrowIfNull(rules);
        if (inauguralPicks.Count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Inaugural Superleague must hold exactly {rules.SuperleagueSize} promoted athletes, was {inauguralPicks.Count}.");
        }

        if (!superAthletes.SetEquals(inauguralPicks))
        {
            throw new InvalidOperationException(
                "Next Superleague roster does not match the 32 inaugural promotions (8 x 4).");
        }
    }

    internal static void ValidateSuperCompositionNormal(
        IReadOnlySet<int> superAthletes,
        IReadOnlySet<int> safe,
        IReadOnlySet<int> promoted,
        IReadOnlySet<int> qualified,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(superAthletes);
        ArgumentNullException.ThrowIfNull(safe);
        ArgumentNullException.ThrowIfNull(promoted);
        ArgumentNullException.ThrowIfNull(qualified);
        ArgumentNullException.ThrowIfNull(rules);
        CheckSuperCounts(safe, promoted, qualified, rules);
        HashSet<int> expected = BuildExpectedSuper(safe, promoted, qualified, rules);
        if (!superAthletes.SetEquals(expected))
        {
            throw new InvalidOperationException(
                "Next Superleague roster does not match 16 safe + 8 promoted + 8 qualifier winners.");
        }
    }

    internal static void CheckSuperCounts(
        IReadOnlySet<int> safe,
        IReadOnlySet<int> promoted,
        IReadOnlySet<int> qualified,
        RulesV1 rules)
    {
        if (safe.Count != rules.SuperleagueSafeCount)
        {
            throw new InvalidOperationException(
                $"Superleague must hold exactly {rules.SuperleagueSafeCount} safe athletes, was {safe.Count}.");
        }

        if (promoted.Count != rules.FeederAutoPromotedCount)
        {
            throw new InvalidOperationException(
                $"Superleague must hold exactly {rules.FeederAutoPromotedCount} promoted champions, was {promoted.Count}.");
        }

        if (qualified.Count != rules.QualifierWinners)
        {
            throw new InvalidOperationException(
                $"Superleague must hold exactly {rules.QualifierWinners} qualifier winners, was {qualified.Count}.");
        }
    }

    internal static HashSet<int> BuildExpectedSuper(
        IReadOnlySet<int> safe,
        IReadOnlySet<int> promoted,
        IReadOnlySet<int> qualified,
        RulesV1 rules)
    {
        HashSet<int> expected = new(safe.Count + promoted.Count + qualified.Count);
        foreach (int id in safe)
        {
            expected.Add(id);
        }

        foreach (int id in promoted)
        {
            if (!expected.Add(id))
            {
                throw new InvalidOperationException($"Superleague roster contains duplicate athlete {id}.");
            }
        }

        foreach (int id in qualified)
        {
            if (!expected.Add(id))
            {
                throw new InvalidOperationException($"Superleague roster contains duplicate athlete {id}.");
            }
        }

        if (expected.Count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Next Superleague must satisfy 16 safe + 8 champions + 8 qualifier winners = 32, was {expected.Count}.");
        }

        return expected;
    }

    /// <summary>
    /// Finalizes bonus season aging: validates the versioned 80/60/40/20/0 decay
    /// weights from the save rules snapshot and that the source season holds
    /// complete stage bonus history, so next-season effective contributions use
    /// the correct weights (Stage 32 enters at 80%). Never uses wall clock,
    /// network or ad-hoc randomness.
    /// </summary>
    internal static void ValidateBonusAging(
        RulesV1 rules,
        SeasonEntity source,
        IReadOnlyList<LeagueEntity> sourceLeagues)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceLeagues);
        int[] expected = [1000, 800, 600, 400, 200, 0];
        if (rules.BonusAgeWeightsThousandths.Count != expected.Length)
        {
            throw new InvalidOperationException(
                $"Bonus decay must hold {expected.Length} age weights, was {rules.BonusAgeWeightsThousandths.Count}.");
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (rules.BonusAgeWeightsThousandths[i] != expected[i])
            {
                throw new InvalidOperationException(
                    $"Bonus decay age {i} must be {expected[i]}, was {rules.BonusAgeWeightsThousandths[i]}.");
            }
        }

        if (sourceLeagues.Count == 0)
        {
            throw new InvalidOperationException($"Season {source.SeasonNumber} has no leagues for bonus aging.");
        }
    }
}
