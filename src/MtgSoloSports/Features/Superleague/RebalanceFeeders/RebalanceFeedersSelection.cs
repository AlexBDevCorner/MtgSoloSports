using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Pure deterministic feeder-rebalancing selection (Game Rules v1 section 12).
/// For each sporting color in enum order, starts from the provisional next-season
/// feeder roster (previous feeder roster minus Superleague entrants plus returning
/// athletes), displaces the lowest-ranked retained athletes when over 32, or draws
/// equal-probability replacements from that color's common pool when under 32.
/// Pool draws shuffle a name-sorted candidate list with the shared versioned RNG
/// and take the prefix, so equivalent RNG state plus population reproduces the
/// equivalent draw. Displacements consume no RNG. Former stars and pool athletes
/// are eligible immediately with no cooldown or performance weighting.
/// </summary>
public static class RebalanceFeedersSelection
{
    public sealed record PoolCandidate(int SaveAthleteId, string Name, SportingColor SportingColor);

    public sealed record RetainedCandidate(int SaveAthleteId, string Name, int SourceRank);

    public sealed record ProvisionalMember(int SaveAthleteId, string Name, bool IsRetained, int SourceRank);

    public sealed record ColorInput(
        SportingColor Color,
        IReadOnlyList<ProvisionalMember> Provisional,
        IReadOnlyList<PoolCandidate> Pool);

    public sealed record ColorPlan(
        SportingColor Color,
        int ProvisionalCount,
        IReadOnlyList<PoolCandidate> Draws,
        IReadOnlyList<RetainedCandidate> Displaced);

    public sealed record RebalancePlan(
        IReadOnlyList<ColorPlan> PerColor,
        IReadOnlyList<PoolCandidate> AllDraws,
        IReadOnlyList<RetainedCandidate> AllDisplaced);

    /// <summary>
    /// Selects draws and displacements for all colors, advancing <paramref name="rng"/>
    /// once per color that needs pool draws (colors in enum order). The caller persists
    /// <c>rng.Snapshot()</c> in the same transaction as memberships and movements.
    /// </summary>
    public static RebalancePlan Select(
        IReadOnlyList<ColorInput> inputs,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        if (inputs.Count != rules.RegularLeagueCount)
        {
            throw new InvalidOperationException(
                $"Rebalancing requires exactly {rules.RegularLeagueCount} color inputs, was {inputs.Count}.");
        }

        List<ColorPlan> plans = new(inputs.Count);
        foreach (ColorInput input in inputs.OrderBy(i => i.Color))
        {
            plans.Add(SelectSingle(input, rng, rules));
        }

        List<PoolCandidate> draws = plans.SelectMany(p => p.Draws).ToList();
        List<RetainedCandidate> displaced = plans.SelectMany(p => p.Displaced).ToList();
        EnsureGlobalUniqueness(plans, draws, displaced);

        return new RebalancePlan(plans, draws, displaced);
    }

    internal static ColorPlan SelectSingle(ColorInput input, Pcg32V1 rng, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);

        if (input.Provisional.Count > rules.LeagueSize)
        {
            return DisplaceOverflow(input, rules);
        }

        if (input.Provisional.Count < rules.LeagueSize)
        {
            return DrawShortfall(input, rng, rules);
        }

        return new ColorPlan(input.Color, input.Provisional.Count, [], []);
    }

    internal static ColorPlan DisplaceOverflow(ColorInput input, RulesV1 rules)
    {
        int excess = input.Provisional.Count - rules.LeagueSize;
        List<RetainedCandidate> retained = input.Provisional
            .Where(m => m.IsRetained)
            .Select(m => new RetainedCandidate(m.SaveAthleteId, m.Name, m.SourceRank))
            .OrderByDescending(c => c.SourceRank)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.SaveAthleteId)
            .ToList();

        if (retained.Count < excess)
        {
            throw new InvalidOperationException(
                $"League {input.Color} overflows by {excess} but holds only {retained.Count} displaceable retained athletes; returning athletes are protected.");
        }

        foreach (RetainedCandidate candidate in retained)
        {
            if (candidate.SourceRank < 1 || candidate.SourceRank > rules.LeagueSize)
            {
                throw new InvalidOperationException(
                    $"Retained athlete {candidate.SaveAthleteId} has corrupt source rank {candidate.SourceRank}.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                throw new InvalidOperationException(
                    $"Retained athlete {candidate.SaveAthleteId} has an empty name.");
            }
        }

        List<RetainedCandidate> displaced = retained.Take(excess).ToList();
        return new ColorPlan(input.Color, input.Provisional.Count, [], displaced);
    }

    internal static ColorPlan DrawShortfall(ColorInput input, Pcg32V1 rng, RulesV1 rules)
    {
        int need = rules.LeagueSize - input.Provisional.Count;
        if (need <= 0)
        {
            throw new InvalidOperationException($"Draw shortfall must be positive, was {need}.");
        }

        List<PoolCandidate> ordered = input.Pool
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.SaveAthleteId)
            .ToList();

        if (ordered.Count < need)
        {
            throw new InvalidOperationException(
                $"League {input.Color} needs {need} pool draws but its common pool holds only {ordered.Count}.");
        }

        foreach (PoolCandidate candidate in ordered)
        {
            if (candidate.SaveAthleteId <= 0)
            {
                throw new InvalidOperationException($"Pool contains invalid athlete id {candidate.SaveAthleteId}.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                throw new InvalidOperationException($"Pool athlete {candidate.SaveAthleteId} has an empty name.");
            }

            if (candidate.SportingColor != input.Color)
            {
                throw new InvalidOperationException(
                    $"Pool athlete {candidate.SaveAthleteId} color {candidate.SportingColor} does not match league {input.Color}.");
            }
        }

        List<PoolCandidate> shuffled = [.. ordered];
        DeterministicShuffle.Shuffle(shuffled, rng);
        List<PoolCandidate> draws = shuffled.Take(need).ToList();
        return new ColorPlan(input.Color, input.Provisional.Count, draws, []);
    }

    private static void EnsureGlobalUniqueness(
        List<ColorPlan> plans,
        List<PoolCandidate> draws,
        List<RetainedCandidate> displaced)
    {
        HashSet<int> seen = new();
        foreach (PoolCandidate draw in draws)
        {
            if (!seen.Add(draw.SaveAthleteId))
            {
                throw new InvalidOperationException($"Rebalancing draws duplicate athlete id {draw.SaveAthleteId}.");
            }
        }

        foreach (RetainedCandidate candidate in displaced)
        {
            if (!seen.Add(candidate.SaveAthleteId))
            {
                throw new InvalidOperationException($"Rebalancing displaces duplicate athlete id {candidate.SaveAthleteId}.");
            }
        }

        foreach (ColorPlan plan in plans)
        {
            if (plan.Draws.Count > 0 && plan.Displaced.Count > 0)
            {
                throw new InvalidOperationException($"League {plan.Color} cannot both draw and displace.");
            }
        }
    }
}
