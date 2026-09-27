using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Structural invariants for feeder rebalancing (MSS-017).
/// Fundamental failures throw and abort the mutation; corrupted sporting state
/// is never silently repaired. Pool vacancies are never filled before all
/// qualifier/movement outcomes are resolved; every next-season feeder member
/// is either retained from the same-color source feeder or returning from the
/// Superleague, and every pool transfer carries a structured movement row.
/// </summary>
public static class RebalanceFeedersInvariants
{
    /// <summary>
    /// Validates the pure plan before persistence: per-color draws come from the
    /// correct color pool, displacements are the lowest-ranked retained athletes,
    /// no athlete is both drawn and displaced, and counts restore exactly 32.
    /// </summary>
    public static void ValidatePlan(
        RebalanceFeedersSelection.RebalancePlan plan,
        IReadOnlyList<RebalanceFeedersSelection.ColorInput> inputs,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);

        if (plan.PerColor.Count != rules.RegularLeagueCount || inputs.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Rebalancing requires exactly {rules.RegularLeagueCount} colors, was {plan.PerColor.Count}.");
        }

        Dictionary<string, RebalanceFeedersSelection.ColorInput> inputsByColor =
            inputs.ToDictionary(i => i.Color.ToString(), StringComparer.Ordinal);
        foreach (RebalanceFeedersSelection.ColorPlan colorPlan in plan.PerColor)
        {
            ValidateSingleColor(colorPlan, inputsByColor, rules);
        }
    }

    /// <summary>
    /// Validates persisted next-season state after rebalancing: eight feeders with
    /// exactly 32 color-matched members each, Superleague still 32, no dual-active
    /// athlete, total memberships preserved, rebalance movements link correctly,
    /// and league history (stage/season standings, rounds, qualifier) untouched.
    /// </summary>
    public static void ValidateCreated(
        SeasonEntity source,
        SeasonEntity next,
        LeagueEntity nextSuperleague,
        IReadOnlyList<LeagueEntity> nextFeeders,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        IReadOnlyList<MovementEntity> rebalanceMovements,
        RebalanceFeedersSelection.RebalancePlan plan,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(nextSuperleague);
        ArgumentNullException.ThrowIfNull(nextFeeders);
        ArgumentNullException.ThrowIfNull(nextMemberships);
        ArgumentNullException.ThrowIfNull(rebalanceMovements);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rules);

        if (next.SeasonNumber != source.SeasonNumber + 1 || !next.HasSuperleague || next.IsComplete)
        {
            throw new InvalidOperationException("Rebalanced next season must be the consecutive incomplete Superleague season.");
        }

        if (nextMemberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Next season must carry exactly {rules.TotalAthletesInSave} memberships, was {nextMemberships.Count}.");
        }

        CheckNoDualActive(nextMemberships);
        CheckFeederSizes(nextMemberships, nextFeeders, nextSuperleague, rules);
        CheckSuperleagueCount(nextMemberships, nextSuperleague, rules);
        CheckFeederColors(nextMemberships, nextFeeders, nextSuperleague);
        CheckMovements(rebalanceMovements, source, next, nextFeeders, plan, rules);
    }

    private static void ValidateSingleColor(
        RebalanceFeedersSelection.ColorPlan colorPlan,
        Dictionary<string, RebalanceFeedersSelection.ColorInput> inputsByColor,
        RulesV1 rules)
    {
        if (!inputsByColor.TryGetValue(colorPlan.Color.ToString(), out RebalanceFeedersSelection.ColorInput? input))
        {
            throw new InvalidOperationException($"Rebalancing plan references unknown color {colorPlan.Color}.");
        }

        if (colorPlan.ProvisionalCount != input.Provisional.Count)
        {
            throw new InvalidOperationException($"League {colorPlan.Color} provisional count is corrupt.");
        }

        int expected = rules.LeagueSize - colorPlan.ProvisionalCount;
        if (expected > 0)
        {
            CheckDrawExpectation(colorPlan, input, expected);
        }
        else if (expected < 0)
        {
            CheckDisplacementExpectation(colorPlan, input, -expected);
        }
        else
        {
            CheckAlreadyBalanced(colorPlan);
        }
    }

    private static void CheckDrawExpectation(
        RebalanceFeedersSelection.ColorPlan colorPlan,
        RebalanceFeedersSelection.ColorInput input,
        int expected)
    {
        if (colorPlan.Draws.Count != expected)
        {
            throw new InvalidOperationException(
                $"League {colorPlan.Color} needs {expected} draws, was {colorPlan.Draws.Count}.");
        }

        if (colorPlan.Displaced.Count != 0)
        {
            throw new InvalidOperationException($"League {colorPlan.Color} cannot both draw and displace.");
        }

        HashSet<int> poolIds = input.Pool.Select(c => c.SaveAthleteId).ToHashSet();
        foreach (RebalanceFeedersSelection.PoolCandidate draw in colorPlan.Draws)
        {
            if (!poolIds.Contains(draw.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Drawn athlete {draw.SaveAthleteId} is not in the {colorPlan.Color} common pool.");
            }
        }
    }

    private static void CheckDisplacementExpectation(
        RebalanceFeedersSelection.ColorPlan colorPlan,
        RebalanceFeedersSelection.ColorInput input,
        int expected)
    {
        if (colorPlan.Displaced.Count != expected)
        {
            throw new InvalidOperationException(
                $"League {colorPlan.Color} must displace {expected} athletes, was {colorPlan.Displaced.Count}.");
        }

        if (colorPlan.Draws.Count != 0)
        {
            throw new InvalidOperationException($"League {colorPlan.Color} cannot both draw and displace.");
        }

        HashSet<int> retainedIds = input.Provisional.Where(m => m.IsRetained).Select(m => m.SaveAthleteId).ToHashSet();
        foreach (RebalanceFeedersSelection.RetainedCandidate displaced in colorPlan.Displaced)
        {
            if (!retainedIds.Contains(displaced.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Displaced athlete {displaced.SaveAthleteId} is not a retained {colorPlan.Color} feeder athlete.");
            }
        }

        CheckDisplacedAreLowest(colorPlan, input);
    }

    private static void CheckAlreadyBalanced(RebalanceFeedersSelection.ColorPlan colorPlan)
    {
        if (colorPlan.Draws.Count != 0 || colorPlan.Displaced.Count != 0)
        {
            throw new InvalidOperationException($"League {colorPlan.Color} is already at 32 and needs no transfers.");
        }
    }

    private static void CheckDisplacedAreLowest(
        RebalanceFeedersSelection.ColorPlan colorPlan,
        RebalanceFeedersSelection.ColorInput input)
    {
        List<RebalanceFeedersSelection.ProvisionalMember> retained = input.Provisional
            .Where(m => m.IsRetained)
            .OrderByDescending(m => m.SourceRank)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.SaveAthleteId)
            .ToList();
        HashSet<int> displacedIds = colorPlan.Displaced.Select(d => d.SaveAthleteId).ToHashSet();
        HashSet<int> expectedIds = retained.Take(colorPlan.Displaced.Count).Select(m => m.SaveAthleteId).ToHashSet();
        if (!displacedIds.SetEquals(expectedIds))
        {
            throw new InvalidOperationException(
                $"League {colorPlan.Color} must displace its lowest-ranked retained athletes.");
        }
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
                    $"Athlete {membership.SaveAthleteId} is active in two leagues after rebalancing.");
            }
        }
    }

    private static void CheckFeederSizes(
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        IReadOnlyList<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague,
        RulesV1 rules)
    {
        foreach (LeagueEntity feeder in nextFeeders)
        {
            int count = nextMemberships.Count(m => m.LeagueId == feeder.Id);
            if (count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{feeder.Name}' must contain exactly {rules.LeagueSize} athletes after rebalancing, was {count}.");
            }
        }

        HashSet<int> feederIds = nextFeeders.Select(l => l.Id).ToHashSet();
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

    private static void CheckSuperleagueCount(
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        LeagueEntity nextSuperleague,
        RulesV1 rules)
    {
        int count = nextMemberships.Count(m => m.LeagueId == nextSuperleague.Id);
        if (count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Next Superleague must still contain exactly {rules.SuperleagueSize} athletes after rebalancing, was {count}.");
        }
    }

    private static void CheckFeederColors(
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        IReadOnlyList<LeagueEntity> nextFeeders,
        LeagueEntity nextSuperleague)
    {
        Dictionary<int, int> colorByLeague = nextFeeders.ToDictionary(l => l.Id, l => l.SportingColor);
        foreach (SeasonMembershipEntity membership in nextMemberships)
        {
            if (membership.LeagueId is null || membership.LeagueId == nextSuperleague.Id)
            {
                continue;
            }

            if (!colorByLeague.TryGetValue(membership.LeagueId.Value, out int expectedColor))
            {
                throw new InvalidOperationException($"Membership {membership.Id} references unknown league {membership.LeagueId}.");
            }

            if (membership.SportingColor != expectedColor)
            {
                throw new InvalidOperationException(
                    $"Athlete {membership.SaveAthleteId} color {membership.SportingColor} does not match feeder color {expectedColor}.");
            }
        }
    }

    private static void CheckMovements(
        IReadOnlyList<MovementEntity> movements,
        SeasonEntity source,
        SeasonEntity next,
        IReadOnlyList<LeagueEntity> nextFeeders,
        RebalanceFeedersSelection.RebalancePlan plan,
        RulesV1 rules)
    {
        _ = rules;
        if (movements.Count != plan.AllDraws.Count + plan.AllDisplaced.Count)
        {
            throw new InvalidOperationException(
                $"Rebalancing must persist exactly {plan.AllDraws.Count + plan.AllDisplaced.Count} movement rows, was {movements.Count}.");
        }

        HashSet<int> feederIds = nextFeeders.Select(l => l.Id).ToHashSet();
        HashSet<int> drawIds = plan.AllDraws.Select(d => d.SaveAthleteId).ToHashSet();
        HashSet<int> displacedIds = plan.AllDisplaced.Select(d => d.SaveAthleteId).ToHashSet();

        HashSet<int> seen = new();
        foreach (MovementEntity movement in movements)
        {
            if (movement.FromSeasonId != source.Id || movement.ToSeasonId != next.Id)
            {
                throw new InvalidOperationException($"Movement {movement.Id} has corrupt season linkage.");
            }

            if (!seen.Add(movement.SaveAthleteId))
            {
                throw new InvalidOperationException($"Rebalancing contains duplicate athlete id {movement.SaveAthleteId}.");
            }

            CheckSingleMovement(movement, feederIds, drawIds, displacedIds);
        }

        if (!drawIds.SetEquals(movements.Where(m => m.Kind == (int)MovementKind.RebalanceDraw).Select(m => m.SaveAthleteId)))
        {
            throw new InvalidOperationException("Persisted pool draws do not match the rebalancing plan.");
        }

        if (!displacedIds.SetEquals(movements.Where(m => m.Kind == (int)MovementKind.RebalanceDisplacement).Select(m => m.SaveAthleteId)))
        {
            throw new InvalidOperationException("Persisted displacements do not match the rebalancing plan.");
        }
    }

    private static void CheckSingleMovement(
        MovementEntity movement,
        HashSet<int> feederIds,
        HashSet<int> drawIds,
        HashSet<int> displacedIds)
    {
        switch ((MovementKind)movement.Kind)
        {
            case MovementKind.RebalanceDraw:
                if (!drawIds.Contains(movement.SaveAthleteId))
                {
                    throw new InvalidOperationException($"Draw {movement.Id} is outside the rebalancing plan.");
                }

                if (movement.FromLeagueId != 0)
                {
                    throw new InvalidOperationException($"Pool draw {movement.Id} must carry pool origin 0.");
                }

                if (!feederIds.Contains(movement.ToLeagueId))
                {
                    throw new InvalidOperationException($"Pool draw {movement.Id} must target its color feeder.");
                }

                if (movement.FromSeasonRank != 0)
                {
                    throw new InvalidOperationException($"Pool draw {movement.Id} must carry source rank 0.");
                }

                break;
            case MovementKind.RebalanceDisplacement:
                if (!displacedIds.Contains(movement.SaveAthleteId))
                {
                    throw new InvalidOperationException($"Displacement {movement.Id} is outside the rebalancing plan.");
                }

                if (!feederIds.Contains(movement.FromLeagueId))
                {
                    throw new InvalidOperationException($"Displacement {movement.Id} must originate from its feeder.");
                }

                if (movement.ToLeagueId != 0)
                {
                    throw new InvalidOperationException($"Displacement {movement.Id} must target pool sentinel 0.");
                }

                if (movement.FromSeasonRank < 1 || movement.FromSeasonRank > 32)
                {
                    throw new InvalidOperationException($"Displacement {movement.Id} has corrupt source rank {movement.FromSeasonRank}.");
                }

                break;
            default:
                throw new InvalidOperationException($"Movement {movement.Id} has unexpected kind {movement.Kind}.");
        }
    }
}
