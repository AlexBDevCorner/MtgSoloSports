using System.Security.Cryptography;
using System.Text;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Universe.CreateUniverse;

/// <summary>
/// Deterministic save-universe selection (Game Rules v1 save universe).
/// Groups catalog candidates by sporting color, sorts each pool by card name
/// (ordinal) and draws exactly <see cref="RulesV1.AthletesPerSportingColor"/>
/// unique athletes per color with the save RNG in sporting-color enum order.
/// The only permitted randomness is <see cref="Pcg32V1"/>; input order, color
/// order and the checksum are fixed so equivalent seed plus catalog reproduces
/// the equivalent universe.
/// </summary>
public static class UniverseSelector
{
    /// <summary>
    /// Selects the universe and advances <paramref name="rng"/>. The caller
    /// persists <c>rng.Snapshot()</c> in the same transaction as the athletes.
    /// </summary>
    public static UniverseSelection Select(
        IReadOnlyList<CatalogAthlete> candidates,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        Dictionary<SportingColor, List<CatalogAthlete>> pools = GroupByColor(candidates);
        EnsureQuotas(pools, rules);

        List<CatalogAthlete> selected = new(rules.TotalAthletesInSave);
        Dictionary<SportingColor, int> counts = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            List<CatalogAthlete> pool = pools[color];
            DeterministicShuffle.Shuffle(pool, rng);
            for (int i = 0; i < rules.AthletesPerSportingColor; i++)
            {
                selected.Add(pool[i]);
            }

            counts[color] = rules.AthletesPerSportingColor;
        }

        UniverseInvariants.ValidateSelection(selected, rules.AthletesPerSportingColor, rules.TotalAthletesInSave);

        UniverseCreationSummary summary = new(
            rules.TotalAthletesInSave,
            counts,
            ComputeChecksum(selected));
        return new UniverseSelection(selected, summary);
    }

    /// <summary>
    /// Fingerprints the selected set: lowercase hex SHA-256 over the globally
    /// name-sorted (ordinal) athlete names joined with newlines. Sorting keeps
    /// the checksum a function of the set, independent of selection order.
    /// SHA-256 is a content fingerprint here, not sporting randomness.
    /// </summary>
    public static string ComputeChecksum(IReadOnlyList<CatalogAthlete> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);

        List<string> names = new(selected.Count);
        foreach (CatalogAthlete athlete in selected)
        {
            names.Add(athlete.Name);
        }

        names.Sort(StringComparer.Ordinal);
        string payload = string.Join('\n', names);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexStringLower(hash);
    }

    private static Dictionary<SportingColor, List<CatalogAthlete>> GroupByColor(IReadOnlyList<CatalogAthlete> candidates)
    {
        Dictionary<SportingColor, List<CatalogAthlete>> pools = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            pools[color] = [];
        }

        foreach (CatalogAthlete candidate in candidates)
        {
            if (candidate is null)
            {
                throw new InvalidOperationException("Catalog candidate must not be null.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                throw new InvalidOperationException("Catalog candidate has an empty name.");
            }

            pools[candidate.SportingColor].Add(candidate);
        }

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            pools[color].Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        }

        return pools;
    }

    private static void EnsureQuotas(Dictionary<SportingColor, List<CatalogAthlete>> pools, RulesV1 rules)
    {
        List<string> deficits = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            int count = pools[color].Count;
            if (count < rules.AthletesPerSportingColor)
            {
                deficits.Add($"{color} has {count}, needs {rules.AthletesPerSportingColor}");
            }
        }

        if (deficits.Count > 0)
        {
            throw new InvalidOperationException(
                $"Catalog cannot supply a save universe: {string.Join("; ", deficits)}.");
        }
    }
}
