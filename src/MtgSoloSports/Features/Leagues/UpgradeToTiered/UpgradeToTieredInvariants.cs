using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Leagues.UpgradeToTiered;

/// <summary>
/// Structural invariants for the v1-to-tiered sporting upgrade. Fundamental
/// failures throw and abort; partial tier creation fails loudly and is never
/// silently repaired. Historical Season/Stage/Round/Standing/Bonus/Cup rows
/// are never rewritten and historical v1 feeders are never reinterpreted.
/// </summary>
public static class UpgradeToTieredInvariants
{
    /// <summary>
    /// Validates the pure plan before persistence: 32 F2 + 32 F3 per color,
    /// all seeds from the correct color pool, no duplicates, no F1 demotion.
    /// </summary>
    public static void ValidatePlan(
        UpgradeToTieredSelection.UpgradePlan plan,
        IReadOnlyList<UpgradeToTieredSelection.ColorInput> inputs,
        IReadOnlySet<int> f1AthleteIds,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(f1AthleteIds);
        ArgumentNullException.ThrowIfNull(rules);

        if (plan.PerColor.Count != rules.RegularLeagueCount || inputs.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Tier upgrade requires exactly {rules.RegularLeagueCount} colors, was {plan.PerColor.Count}.");
        }

        if (plan.AllF2.Count != rules.RegularLeagueCount * rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Tier upgrade must seed exactly {rules.RegularLeagueCount * rules.LeagueSize} F2 athletes, was {plan.AllF2.Count}.");
        }

        if (plan.AllF3.Count != rules.RegularLeagueCount * rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"Tier upgrade must seed exactly {rules.RegularLeagueCount * rules.LeagueSize} F3 athletes, was {plan.AllF3.Count}.");
        }

        Dictionary<string, UpgradeToTieredSelection.ColorInput> byColor =
            inputs.ToDictionary(i => i.Color.ToString(), StringComparer.Ordinal);
        foreach (UpgradeToTieredSelection.ColorPlan colorPlan in plan.PerColor)
        {
            ValidateSingleColor(colorPlan, byColor, f1AthleteIds, rules);
        }
    }

    internal static void ValidateSingleColor(
        UpgradeToTieredSelection.ColorPlan colorPlan,
        Dictionary<string, UpgradeToTieredSelection.ColorInput> byColor,
        IReadOnlySet<int> f1AthleteIds,
        RulesV1 rules)
    {
        if (!byColor.TryGetValue(colorPlan.Color.ToString(), out UpgradeToTieredSelection.ColorInput? input))
        {
            throw new InvalidOperationException($"Tier upgrade references unknown color {colorPlan.Color}.");
        }

        if (colorPlan.F2.Count != rules.LeagueSize || colorPlan.F3.Count != rules.LeagueSize)
        {
            throw new InvalidOperationException(
                $"League {colorPlan.Color} must seed exactly {rules.LeagueSize} F2 and {rules.LeagueSize} F3, was {colorPlan.F2.Count}/{colorPlan.F3.Count}.");
        }

        HashSet<int> poolIds = input.Pool.Select(c => c.SaveAthleteId).ToHashSet();
        foreach (UpgradeToTieredSelection.PoolCandidate seed in colorPlan.F2.Concat(colorPlan.F3))
        {
            if (!poolIds.Contains(seed.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Seeded athlete {seed.SaveAthleteId} is not in the {colorPlan.Color} common pool.");
            }

            if (f1AthleteIds.Contains(seed.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Seeded athlete {seed.SaveAthleteId} is an F1 athlete; F1 is never demoted to populate F2/F3.");
            }
        }
    }

    /// <summary>
    /// Validates persisted next-season state after the upgrade: 24 feeders
    /// (F1 preserved at 32 each, F2/F3 at 32 each), Superleague still 32, no
    /// dual-active athlete, pool counts correct, and upgrade movements link.
    /// </summary>
    public static void ValidateCreated(
        SeasonEntity source,
        SeasonEntity next,
        LeagueEntity nextSuperleague,
        IReadOnlyList<LeagueEntity> nextFeeders,
        IReadOnlyList<SeasonMembershipEntity> nextMemberships,
        IReadOnlyList<MovementEntity> upgradeMovements,
        UpgradeToTieredSelection.UpgradePlan plan,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(nextSuperleague);
        ArgumentNullException.ThrowIfNull(nextFeeders);
        ArgumentNullException.ThrowIfNull(nextMemberships);
        ArgumentNullException.ThrowIfNull(upgradeMovements);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rules);

        if (next.SeasonNumber != source.SeasonNumber + 1 || !next.HasSuperleague || next.IsComplete)
        {
            throw new InvalidOperationException("Upgraded next season must be the consecutive incomplete Superleague season.");
        }

        if (nextFeeders.Count != rules.TieredFeederLeagueCount)
        {
            throw new InvalidOperationException(
                $"Upgraded season must have exactly {rules.TieredFeederLeagueCount} feeder leagues, was {nextFeeders.Count}.");
        }

        if (nextMemberships.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Upgraded season must carry exactly {rules.TotalAthletesInSave} memberships, was {nextMemberships.Count}.");
        }

        CheckNoDualActive(nextMemberships);
        CheckFeederSizes(nextMemberships, nextFeeders, rules);
        CheckSuperleagueCount(nextMemberships, nextSuperleague, rules);
        CheckMovements(upgradeMovements, source, next, plan, rules);
    }

    private static void CheckNoDualActive(IReadOnlyList<SeasonMembershipEntity> memberships)
    {
        HashSet<int> active = new();
        foreach (SeasonMembershipEntity membership in memberships)
        {
            if (membership.LeagueId is null)
            {
                continue;
            }

            if (!active.Add(membership.SaveAthleteId))
            {
                throw new InvalidOperationException(
                    $"Athlete {membership.SaveAthleteId} is active in two leagues after tier upgrade.");
            }
        }
    }

    private static void CheckFeederSizes(
        IReadOnlyList<SeasonMembershipEntity> memberships,
        IReadOnlyList<LeagueEntity> feeders,
        RulesV1 rules)
    {
        foreach (LeagueEntity feeder in feeders)
        {
            int count = memberships.Count(m => m.LeagueId == feeder.Id);
            if (count != rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"League '{feeder.Name}' must contain exactly {rules.LeagueSize} athletes after tier upgrade, was {count}.");
            }
        }
    }

    private static void CheckSuperleagueCount(
        IReadOnlyList<SeasonMembershipEntity> memberships,
        LeagueEntity superleague,
        RulesV1 rules)
    {
        int count = memberships.Count(m => m.LeagueId == superleague.Id);
        if (count != rules.SuperleagueSize)
        {
            throw new InvalidOperationException(
                $"Superleague must still contain exactly {rules.SuperleagueSize} athletes after tier upgrade, was {count}.");
        }
    }

    private static void CheckMovements(
        IReadOnlyList<MovementEntity> movements,
        SeasonEntity source,
        SeasonEntity next,
        UpgradeToTieredSelection.UpgradePlan plan,
        RulesV1 rules)
    {
        _ = rules;
        int expected = plan.AllF2.Count + plan.AllF3.Count;
        if (movements.Count != expected)
        {
            throw new InvalidOperationException(
                $"Tier upgrade must persist exactly {expected} movement rows, was {movements.Count}.");
        }

        HashSet<int> seen = new();
        foreach (MovementEntity movement in movements)
        {
            if (movement.FromSeasonId != source.Id || movement.ToSeasonId != next.Id)
            {
                throw new InvalidOperationException($"Movement {movement.Id} has corrupt season linkage.");
            }

            if (movement.Kind != (int)MovementKind.TierUpgradeSeed)
            {
                throw new InvalidOperationException($"Movement {movement.Id} has unexpected kind {movement.Kind}.");
            }

            if (!seen.Add(movement.SaveAthleteId))
            {
                throw new InvalidOperationException($"Tier upgrade contains duplicate athlete id {movement.SaveAthleteId}.");
            }
        }
    }
}
