using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Leagues.UpgradeToTiered;

/// <summary>
/// Pure deterministic F2/F3 seeding for the v1-to-tiered sporting upgrade.
/// For each sporting color in enum order, name-sorts that color's current
/// target-season pool, shuffles with the shared versioned RNG, and assigns the
/// first 32 to F2 and the next 32 to F3 (remaining stay in the pool). This is
/// initial population creation, so equal-probability draw is the analogue of
/// Season 1/pool behavior; bonus/rating/strength never seed divisions and F1
/// athletes are never demoted to populate new divisions.
/// </summary>
public static class UpgradeToTieredSelection
{
    public sealed record PoolCandidate(int SaveAthleteId, string Name, SportingColor SportingColor, int DrawIndex);

    public sealed record ColorInput(SportingColor Color, IReadOnlyList<PoolCandidate> Pool);

    public sealed record ColorPlan(
        SportingColor Color,
        IReadOnlyList<PoolCandidate> F2,
        IReadOnlyList<PoolCandidate> F3);

    public sealed record UpgradePlan(
        IReadOnlyList<ColorPlan> PerColor,
        IReadOnlyList<PoolCandidate> AllF2,
        IReadOnlyList<PoolCandidate> AllF3);

    /// <summary>
    /// Selects F2/F3 seeds for all colors, advancing <paramref name="rng"/>
    /// once per color in enum order. The caller persists
    /// <c>rng.Snapshot()</c> in the same transaction as leagues/memberships.
    /// </summary>
    public static UpgradePlan Select(
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
                $"Tier upgrade requires exactly {rules.RegularLeagueCount} color inputs, was {inputs.Count}.");
        }

        List<ColorPlan> plans = new(inputs.Count);
        foreach (ColorInput input in inputs.OrderBy(i => i.Color))
        {
            plans.Add(SelectSingle(input, rng, rules));
        }

        List<PoolCandidate> f2 = plans.SelectMany(p => p.F2).ToList();
        List<PoolCandidate> f3 = plans.SelectMany(p => p.F3).ToList();
        EnsureGlobalUniqueness(f2, f3);

        return new UpgradePlan(plans, f2, f3);
    }

    internal static ColorPlan SelectSingle(ColorInput input, Pcg32V1 rng, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);

        const int perDivision = 32;
        List<PoolCandidate> ordered = input.Pool
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.SaveAthleteId)
            .ToList();

        if (ordered.Count < 2 * perDivision)
        {
            throw new InvalidOperationException(
                $"League {input.Color} needs 64 pool athletes to seed F2/F3 but its pool holds only {ordered.Count}.");
        }

        ValidateCandidates(input, ordered);

        List<PoolCandidate> shuffled = [.. ordered];
        DeterministicShuffle.Shuffle(shuffled, rng);
        List<PoolCandidate> f2 = shuffled.Take(perDivision).ToList();
        List<PoolCandidate> f3 = shuffled.Skip(perDivision).Take(perDivision).ToList();
        return new ColorPlan(input.Color, f2, f3);
    }

    private static void ValidateCandidates(ColorInput input, List<PoolCandidate> ordered)
    {
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
                    $"Pool athlete {candidate.SaveAthleteId} color {candidate.SportingColor} does not match pool {input.Color}.");
            }
        }
    }

    private static void EnsureGlobalUniqueness(List<PoolCandidate> f2, List<PoolCandidate> f3)
    {
        HashSet<int> seen = new();
        foreach (PoolCandidate candidate in f2.Concat(f3))
        {
            if (!seen.Add(candidate.SaveAthleteId))
            {
                throw new InvalidOperationException($"Tier upgrade seeds duplicate athlete id {candidate.SaveAthleteId}.");
            }
        }
    }
}
