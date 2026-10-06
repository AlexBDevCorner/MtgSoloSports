using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Structural invariants for tier-cascade feeder rebalancing (MSS-059).
/// Fundamental failures throw and abort the mutation; corrupted sporting state
/// is never silently repaired. Pool vacancies are never filled before all
/// qualifier/movement outcomes are resolved; every next-season feeder holds
/// exactly 32 color-matched athletes across all 24 F1/F2/F3 leagues; the pool
/// only ever connects directly to F3 (no Pool↔F1/F2, no F1/F2↔Pool rows);
/// structural F1↔F2/F2↔F3 moves are adjacent-tier only with deterministic
/// best/worst retained-first ordering; every pool and structural transfer
/// carries a structured movement row.
/// </summary>
public static class RebalanceFeedersInvariants
{
    /// <summary>
    /// Validates the tier cascade plan before persistence: per-color draws come
    /// from the correct color pool into F3 only, displacements originate from
    /// F3 only, structural moves are adjacent-tier same-color single moves,
    /// upward moves use the best eligible retained athletes and downward moves
    /// the worst, no athlete moves twice, and every division restores 32.
    /// </summary>
    public static void ValidatePlan(
        RebalanceFeedersSelection.RebalancePlan plan,
        IReadOnlyList<RebalanceFeedersSelection.TierColorInput> inputs,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rules);

        if (plan.TierPerColor.Count != rules.RegularLeagueCount || inputs.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Rebalancing requires exactly {rules.RegularLeagueCount} colors, was {plan.TierPerColor.Count}.");
        }

        Dictionary<string, RebalanceFeedersSelection.TierColorInput> inputsByColor =
            inputs.ToDictionary(i => i.Color.ToString(), StringComparer.Ordinal);
        foreach (RebalanceFeedersSelection.TierColorPlan colorPlan in plan.TierPerColor)
        {
            ValidateSingleTierColor(colorPlan, inputsByColor, rules);
        }
    }

    /// <summary>
    /// Legacy single-feeder plan validation (pre-tier). Retained for unit-test
    /// compatibility.
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
    /// Validates persisted next-season state after rebalancing: all 24 feeder
    /// leagues with exactly 32 color-matched members each, Superleague still
    /// 32, no dual-active athlete, total memberships preserved, rebalance
    /// movements link correctly with pool boundary only at F3, and league
    /// history (stage/season standings, rounds, qualifier) untouched.
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

        if (nextFeeders.Count != rules.TieredFeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Season {next.SeasonNumber} must have exactly {rules.TieredFeederLeagueCount} feeder leagues, was {nextFeeders.Count}.");
        }

        CheckNoDualActive(nextMemberships);
        CheckFeederSizes(nextMemberships, nextFeeders, nextSuperleague, rules);
        CheckSuperleagueCount(nextMemberships, nextSuperleague, rules);
        CheckFeederColors(nextMemberships, nextFeeders, nextSuperleague);
        CheckMovements(rebalanceMovements, source, next, nextFeeders, plan, rules);
    }

    private static void ValidateSingleTierColor(
        RebalanceFeedersSelection.TierColorPlan colorPlan,
        Dictionary<string, RebalanceFeedersSelection.TierColorInput> inputsByColor,
        RulesV1 rules)
    {
        if (!inputsByColor.TryGetValue(colorPlan.Color.ToString(), out RebalanceFeedersSelection.TierColorInput? input))
        {
            throw new InvalidOperationException($"Rebalancing plan references unknown color {colorPlan.Color}.");
        }

        if (colorPlan.F1ProvisionalCount != input.F1.Count
            || colorPlan.F2ProvisionalCount != input.F2.Count
            || colorPlan.F3ProvisionalCount != input.F3.Count)
        {
            throw new InvalidOperationException($"League {colorPlan.Color} provisional counts are corrupt.");
        }

        // Final sizes must restore 32 per division.
        int f1Final = colorPlan.F1ProvisionalCount
            + CountTo(colorPlan.UpMoves, FeederDivision.First)
            - CountFrom(colorPlan.DownMoves, FeederDivision.First);
        int f2Final = colorPlan.F2ProvisionalCount
            + CountTo(colorPlan.UpMoves, FeederDivision.Second)
            + CountTo(colorPlan.DownMoves, FeederDivision.Second)
            - CountFrom(colorPlan.UpMoves, FeederDivision.Second)
            - CountFrom(colorPlan.DownMoves, FeederDivision.Second);
        int f3Final = colorPlan.F3ProvisionalCount
            + CountTo(colorPlan.DownMoves, FeederDivision.Third)
            - CountFrom(colorPlan.UpMoves, FeederDivision.Third)
            + colorPlan.Draws.Count
            - colorPlan.Displaced.Count;
        if (f1Final != rules.LeagueSize || f2Final != rules.LeagueSize || f3Final != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"League {colorPlan.Color} cascade must restore 32/32/32, was {f1Final}/{f2Final}/{f3Final}.");
        }

        CheckTierDraws(colorPlan, input);
        CheckTierDisplaced(colorPlan, input, rules);
        CheckTierStructural(colorPlan, input, rules);
    }

    private static int CountTo(
        IReadOnlyList<RebalanceFeedersSelection.StructuralMove> moves, FeederDivision division)
    {
        int count = 0;
        foreach (RebalanceFeedersSelection.StructuralMove move in moves)
        {
            if (move.ToDivision == division)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountFrom(
        IReadOnlyList<RebalanceFeedersSelection.StructuralMove> moves, FeederDivision division)
    {
        int count = 0;
        foreach (RebalanceFeedersSelection.StructuralMove move in moves)
        {
            if (move.FromDivision == division)
            {
                count++;
            }
        }

        return count;
    }

    private static void CheckTierDraws(
        RebalanceFeedersSelection.TierColorPlan colorPlan,
        RebalanceFeedersSelection.TierColorInput input)
    {
        HashSet<int> poolIds = input.Pool.Select(c => c.SaveAthleteId).ToHashSet();
        foreach (RebalanceFeedersSelection.PoolCandidate draw in colorPlan.Draws)
        {
            if (!poolIds.Contains(draw.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Drawn athlete {draw.SaveAthleteId} is not in the {colorPlan.Color} common pool.");
            }

            if (draw.SportingColor != colorPlan.Color)
            {
                throw new InvalidOperationException(
                    $"Drawn athlete {draw.SaveAthleteId} color {draw.SportingColor} does not match league {colorPlan.Color}.");
            }
        }
    }

    private static void CheckTierDisplaced(
        RebalanceFeedersSelection.TierColorPlan colorPlan,
        RebalanceFeedersSelection.TierColorInput input,
        RulesV1 rules)
    {
        _ = rules;
        HashSet<int> f3Ids = input.F3.Select(m => m.SaveAthleteId).ToHashSet();
        foreach (RebalanceFeedersSelection.RetainedCandidate displaced in colorPlan.Displaced)
        {
            if (!f3Ids.Contains(displaced.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Displaced athlete {displaced.SaveAthleteId} is not a provisional {colorPlan.Color} F3 athlete; only F3 may displace to pool.");
            }
        }

        CheckTierDisplacedAreWorst(colorPlan, input);
    }

    private static void CheckTierDisplacedAreWorst(
        RebalanceFeedersSelection.TierColorPlan colorPlan,
        RebalanceFeedersSelection.TierColorInput input)
    {
        // Displacements must be the worst eligible retained F3 athletes first:
        // retained (unprotected) worst-first, then protected worst-first, with
        // name/id tie breaks, excluding athletes already moved F2→F3 in this
        // same cascade (they move at most once).
        HashSet<int> arrived = colorPlan.DownMoves
            .Where(m => m.FromDivision == FeederDivision.Second && m.ToDivision == FeederDivision.Third)
            .Select(m => m.SaveAthleteId)
            .ToHashSet();
        List<RebalanceFeedersSelection.TierMember> eligible = input.F3
            .Where(m => !arrived.Contains(m.SaveAthleteId))
            .OrderBy(m => m.IsProtected)
            .ThenByDescending(m => m.SourceRank)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.SaveAthleteId)
            .ToList();
        HashSet<int> expected = eligible.Take(colorPlan.Displaced.Count).Select(m => m.SaveAthleteId).ToHashSet();
        HashSet<int> actual = colorPlan.Displaced.Select(d => d.SaveAthleteId).ToHashSet();
        if (!actual.SetEquals(expected))
        {
            throw new InvalidOperationException(
                $"League {colorPlan.Color} F3 must displace its worst eligible retained athletes first.");
        }
    }

    private static void CheckTierStructural(
        RebalanceFeedersSelection.TierColorPlan colorPlan,
        RebalanceFeedersSelection.TierColorInput input,
        RulesV1 rules)
    {
        _ = rules;
        Dictionary<int, RebalanceFeedersSelection.TierMember> f1ById = input.F1.ToDictionary(m => m.SaveAthleteId);
        Dictionary<int, RebalanceFeedersSelection.TierMember> f2ById = input.F2.ToDictionary(m => m.SaveAthleteId);
        Dictionary<int, RebalanceFeedersSelection.TierMember> f3ById = input.F3.ToDictionary(m => m.SaveAthleteId);

        foreach (RebalanceFeedersSelection.StructuralMove move in colorPlan.UpMoves.Concat(colorPlan.DownMoves))
        {
            if (move.Color != colorPlan.Color)
            {
                throw new InvalidOperationException(
                    $"Structural move {move.SaveAthleteId} color {move.Color} does not match league {colorPlan.Color}; no cross-color movement.");
            }

            if (Math.Abs((int)move.ToDivision - (int)move.FromDivision) != 1)
            {
                throw new InvalidOperationException(
                    $"Structural move {move.SaveAthleteId} must be between adjacent tiers.");
            }

            Dictionary<int, RebalanceFeedersSelection.TierMember> sourceMap = move.FromDivision switch
            {
                FeederDivision.First => f1ById,
                FeederDivision.Second => f2ById,
                FeederDivision.Third => f3ById,
                _ => throw new InvalidOperationException($"Structural move {move.SaveAthleteId} has corrupt division."),
            };
            if (!sourceMap.TryGetValue(move.SaveAthleteId, out RebalanceFeedersSelection.TierMember? member))
            {
                throw new InvalidOperationException(
                    $"Structural move {move.SaveAthleteId} is not a provisional {move.FromDivision} athlete.");
            }

            if (member.SourceRank != move.SourceRank)
            {
                throw new InvalidOperationException(
                    $"Structural move {move.SaveAthleteId} source rank {move.SourceRank} does not match provisional rank {member.SourceRank}.");
            }
        }

        CheckUpOrdering(colorPlan, input);
        CheckDownOrdering(colorPlan, input);
    }

    private static void CheckUpOrdering(
        RebalanceFeedersSelection.TierColorPlan colorPlan,
        RebalanceFeedersSelection.TierColorInput input)
    {
        // F2→F1 up-moves must be the best eligible retained F2 athletes.
        List<RebalanceFeedersSelection.StructuralMove> f2ToF1 = colorPlan.UpMoves
            .Where(m => m.FromDivision == FeederDivision.Second && m.ToDivision == FeederDivision.First)
            .ToList();
        if (f2ToF1.Count > 0)
        {
            List<int> expected = input.F2
                .OrderBy(m => m.IsProtected)
                .ThenBy(m => m.SourceRank)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => m.SaveAthleteId)
                .Take(f2ToF1.Count)
                .Select(m => m.SaveAthleteId)
                .OrderBy(id => id)
                .ToList();
            List<int> actual = f2ToF1.Select(m => m.SaveAthleteId).OrderBy(id => id).ToList();
            if (!actual.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"League {colorPlan.Color} F2→F1 must pull its best eligible retained athletes first.");
            }
        }

        // F3→F2 up-moves must be the best eligible retained F3 athletes.
        List<RebalanceFeedersSelection.StructuralMove> f3ToF2 = colorPlan.UpMoves
            .Where(m => m.FromDivision == FeederDivision.Third && m.ToDivision == FeederDivision.Second)
            .ToList();
        if (f3ToF2.Count > 0)
        {
            List<int> expected = input.F3
                .OrderBy(m => m.IsProtected)
                .ThenBy(m => m.SourceRank)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => m.SaveAthleteId)
                .Take(f3ToF2.Count)
                .Select(m => m.SaveAthleteId)
                .OrderBy(id => id)
                .ToList();
            List<int> actual = f3ToF2.Select(m => m.SaveAthleteId).OrderBy(id => id).ToList();
            if (!actual.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"League {colorPlan.Color} F3→F2 must pull its best eligible retained athletes first.");
            }
        }
    }

    private static void CheckDownOrdering(
        RebalanceFeedersSelection.TierColorPlan colorPlan,
        RebalanceFeedersSelection.TierColorInput input)
    {
        List<RebalanceFeedersSelection.StructuralMove> f1ToF2 = colorPlan.DownMoves
            .Where(m => m.FromDivision == FeederDivision.First && m.ToDivision == FeederDivision.Second)
            .ToList();
        if (f1ToF2.Count > 0)
        {
            List<int> expected = input.F1
                .OrderBy(m => m.IsProtected)
                .ThenByDescending(m => m.SourceRank)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => m.SaveAthleteId)
                .Take(f1ToF2.Count)
                .Select(m => m.SaveAthleteId)
                .OrderBy(id => id)
                .ToList();
            List<int> actual = f1ToF2.Select(m => m.SaveAthleteId).OrderBy(id => id).ToList();
            if (!actual.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"League {colorPlan.Color} F1→F2 must push its worst eligible retained athletes first.");
            }
        }

        List<RebalanceFeedersSelection.StructuralMove> f2ToF3 = colorPlan.DownMoves
            .Where(m => m.FromDivision == FeederDivision.Second && m.ToDivision == FeederDivision.Third)
            .ToList();
        if (f2ToF3.Count > 0)
        {
            HashSet<int> arrived = colorPlan.DownMoves
                .Where(m => m.FromDivision == FeederDivision.First && m.ToDivision == FeederDivision.Second)
                .Select(m => m.SaveAthleteId)
                .ToHashSet();
            List<int> expected = input.F2
                .Where(m => !arrived.Contains(m.SaveAthleteId))
                .OrderBy(m => m.IsProtected)
                .ThenByDescending(m => m.SourceRank)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => m.SaveAthleteId)
                .Take(f2ToF3.Count)
                .Select(m => m.SaveAthleteId)
                .OrderBy(id => id)
                .ToList();
            List<int> actual = f2ToF3.Select(m => m.SaveAthleteId).OrderBy(id => id).ToList();
            if (!actual.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"League {colorPlan.Color} F2→F3 must push its worst eligible retained athletes first (never re-moving F1 arrivals).");
            }
        }
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
        int expectedCount = plan.AllDraws.Count + plan.AllDisplaced.Count
            + plan.AllUpMoves.Count + plan.AllDownMoves.Count;
        if (movements.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"Rebalancing must persist exactly {expectedCount} movement rows, was {movements.Count}.");
        }

        HashSet<int> feederIds = nextFeeders.Select(l => l.Id).ToHashSet();
        Dictionary<int, LeagueEntity> feedersById = nextFeeders.ToDictionary(l => l.Id);
        HashSet<int> drawIds = plan.AllDraws.Select(d => d.SaveAthleteId).ToHashSet();
        HashSet<int> displacedIds = plan.AllDisplaced.Select(d => d.SaveAthleteId).ToHashSet();
        HashSet<int> upIds = plan.AllUpMoves.Select(m => m.SaveAthleteId).ToHashSet();
        HashSet<int> downIds = plan.AllDownMoves.Select(m => m.SaveAthleteId).ToHashSet();

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

            CheckSingleMovement(movement, feederIds, feedersById, drawIds, displacedIds, upIds, downIds, rules);
        }

        CheckPersistedSets(movements, drawIds, displacedIds, upIds, downIds);
    }

    private static void CheckPersistedSets(
        IReadOnlyList<MovementEntity> movements,
        HashSet<int> drawIds,
        HashSet<int> displacedIds,
        HashSet<int> upIds,
        HashSet<int> downIds)
    {
        if (!drawIds.SetEquals(movements.Where(m => m.Kind == (int)MovementKind.RebalanceDraw).Select(m => m.SaveAthleteId)))
        {
            throw new InvalidOperationException("Persisted pool draws do not match the rebalancing plan.");
        }

        if (!displacedIds.SetEquals(movements.Where(m => m.Kind == (int)MovementKind.RebalanceDisplacement).Select(m => m.SaveAthleteId)))
        {
            throw new InvalidOperationException("Persisted displacements do not match the rebalancing plan.");
        }

        HashSet<int> persistedUp = movements.Where(m => m.Kind == (int)MovementKind.RebalanceUp).Select(m => m.SaveAthleteId).ToHashSet();
        if (!upIds.SetEquals(persistedUp))
        {
            throw new InvalidOperationException("Persisted structural up-moves do not match the rebalancing plan.");
        }

        HashSet<int> persistedDown = movements.Where(m => m.Kind == (int)MovementKind.RebalanceDown).Select(m => m.SaveAthleteId).ToHashSet();
        if (!downIds.SetEquals(persistedDown))
        {
            throw new InvalidOperationException("Persisted structural down-moves do not match the rebalancing plan.");
        }
    }

    private static void CheckSingleMovement(
        MovementEntity movement,
        HashSet<int> feederIds,
        Dictionary<int, LeagueEntity> feedersById,
        HashSet<int> drawIds,
        HashSet<int> displacedIds,
        HashSet<int> upIds,
        HashSet<int> downIds,
        RulesV1 rules)
    {
        switch ((MovementKind)movement.Kind)
        {
            case MovementKind.RebalanceDraw:
                CheckDrawMovement(movement, feederIds, feedersById, drawIds, rules);
                break;
            case MovementKind.RebalanceDisplacement:
                CheckDisplacementMovement(movement, feederIds, feedersById, displacedIds, rules);
                break;
            case MovementKind.RebalanceUp:
            case MovementKind.RebalanceDown:
                CheckStructuralMovement(movement, feederIds, feedersById, upIds, downIds);
                break;
            default:
                throw new InvalidOperationException($"Movement {movement.Id} has unexpected kind {movement.Kind}.");
        }
    }

    private static void CheckDrawMovement(
        MovementEntity movement,
        HashSet<int> feederIds,
        Dictionary<int, LeagueEntity> feedersById,
        HashSet<int> drawIds,
        RulesV1 rules)
    {
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

        // Tiered saves: pool connects only to F3. Legacy v1 single-feeder
        // saves have only the F1 feeder, so any feeder target is legal.
        bool tiered = rules.FeederDivisionsPerColor == 3;
        if (tiered
            && feedersById.TryGetValue(movement.ToLeagueId, out LeagueEntity? drawTo)
            && drawTo.FeederDivision != (int)FeederDivision.Third)
        {
            throw new InvalidOperationException(
                $"Pool draw {movement.Id} must target F3 only; pool must not bypass feeder levels.");
        }

        if (movement.FromSeasonRank != 0)
        {
            throw new InvalidOperationException($"Pool draw {movement.Id} must carry source rank 0.");
        }
    }

    private static void CheckDisplacementMovement(
        MovementEntity movement,
        HashSet<int> feederIds,
        Dictionary<int, LeagueEntity> feedersById,
        HashSet<int> displacedIds,
        RulesV1 rules)
    {
        if (!displacedIds.Contains(movement.SaveAthleteId))
        {
            throw new InvalidOperationException($"Displacement {movement.Id} is outside the rebalancing plan.");
        }

        if (!feederIds.Contains(movement.FromLeagueId))
        {
            throw new InvalidOperationException($"Displacement {movement.Id} must originate from its feeder.");
        }

        // Tiered saves: only F3 may displace to pool. Legacy v1
        // single-feeder saves displace from their only feeder.
        bool tiered = rules.FeederDivisionsPerColor == 3;
        if (tiered
            && feedersById.TryGetValue(movement.FromLeagueId, out LeagueEntity? dispFrom)
            && dispFrom.FeederDivision != (int)FeederDivision.Third)
        {
            throw new InvalidOperationException(
                $"Displacement {movement.Id} must originate from F3 only; F1/F2 must never displace directly to pool.");
        }

        if (movement.ToLeagueId != 0)
        {
            throw new InvalidOperationException($"Displacement {movement.Id} must target pool sentinel 0.");
        }

        if (movement.FromSeasonRank < 1 || movement.FromSeasonRank > 32)
        {
            throw new InvalidOperationException($"Displacement {movement.Id} has corrupt source rank {movement.FromSeasonRank}.");
        }
    }

    private static void CheckStructuralMovement(
        MovementEntity movement,
        HashSet<int> feederIds,
        Dictionary<int, LeagueEntity> feedersById,
        HashSet<int> upIds,
        HashSet<int> downIds)
    {
        HashSet<int> planIds = (MovementKind)movement.Kind == MovementKind.RebalanceUp ? upIds : downIds;
        if (!planIds.Contains(movement.SaveAthleteId))
        {
            throw new InvalidOperationException($"Structural move {movement.Id} is outside the rebalancing plan.");
        }

        if (!feederIds.Contains(movement.FromLeagueId) || !feederIds.Contains(movement.ToLeagueId))
        {
            throw new InvalidOperationException(
                $"Structural move {movement.Id} must be between two feeder leagues; pool sentinel is forbidden.");
        }

        if (movement.FromLeagueId == 0 || movement.ToLeagueId == 0)
        {
            throw new InvalidOperationException(
                $"Structural move {movement.Id} must never touch the common pool directly.");
        }

        if (!feedersById.TryGetValue(movement.FromLeagueId, out LeagueEntity? from)
            || !feedersById.TryGetValue(movement.ToLeagueId, out LeagueEntity? to))
        {
            throw new InvalidOperationException($"Structural move {movement.Id} references unknown leagues.");
        }

        CheckStructuralLeagues(movement, from, to);
    }

    private static void CheckStructuralLeagues(MovementEntity movement, LeagueEntity from, LeagueEntity to)
    {
        if (from.SportingColor != to.SportingColor || from.SportingColor != movement.SportingColor)
        {
            throw new InvalidOperationException(
                $"Structural move {movement.Id} must preserve sporting color; no cross-color movement.");
        }

        if (Math.Abs(from.FeederDivision - to.FeederDivision) != 1)
        {
            throw new InvalidOperationException(
                $"Structural move {movement.Id} must be between adjacent tiers.");
        }

        if ((MovementKind)movement.Kind == MovementKind.RebalanceUp && to.FeederDivision >= from.FeederDivision)
        {
            throw new InvalidOperationException($"RebalanceUp {movement.Id} must move to a higher tier.");
        }

        if ((MovementKind)movement.Kind == MovementKind.RebalanceDown && to.FeederDivision <= from.FeederDivision)
        {
            throw new InvalidOperationException($"RebalanceDown {movement.Id} must move to a lower tier.");
        }

        if (movement.FromSeasonRank < 1 || movement.FromSeasonRank > 32)
        {
            throw new InvalidOperationException($"Structural move {movement.Id} has corrupt source rank {movement.FromSeasonRank}.");
        }
    }
}
