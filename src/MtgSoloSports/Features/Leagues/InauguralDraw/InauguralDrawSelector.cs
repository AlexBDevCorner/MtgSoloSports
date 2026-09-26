using System.Security.Cryptography;
using System.Text;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Leagues.InauguralDraw;

/// <summary>
/// Deterministic Season 1 inaugural draw (Game Rules v1 Season 1).
/// For each sporting color in enum order, sorts that color's 256-athlete save
/// population by card name (ordinal) and shuffles with the save RNG; the first
/// <see cref="RulesV1.LeagueSize"/> athletes in shuffled order join the league in
/// that order (the persisted reveal order), the rest remain in the common pool.
/// The only permitted randomness is <see cref="Pcg32V1"/>; input sorting and
/// color order are fixed so equivalent RNG state plus population reproduces the
/// equivalent draw.
/// </summary>
public static class InauguralDrawSelector
{
    /// <summary>
    /// Draws Season 1 leagues and advances <paramref name="rng"/>. The caller
    /// persists <c>rng.Snapshot()</c> in the same transaction as the membership rows.
    /// </summary>
    public static InauguralDrawResult Select(
        IReadOnlyList<CatalogAthlete> savePopulation,
        Pcg32V1 rng,
        RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(savePopulation);
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentNullException.ThrowIfNull(rules);
        rules.Validate();

        Dictionary<SportingColor, List<CatalogAthlete>> pools = GroupByColor(savePopulation);
        EnsureSaveQuotas(pools, rules);

        List<InauguralDrawEntry> entries = new(rules.TotalAthletesInSave);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            List<CatalogAthlete> pool = pools[color];
            DeterministicShuffle.Shuffle(pool, rng);
            for (int i = 0; i < pool.Count; i++)
            {
                bool member = i < rules.LeagueSize;
                entries.Add(new InauguralDrawEntry(pool[i].Name, color, i, member));
            }
        }

        Season1Invariants.ValidateDrawResult(entries, rules);

        string checksum = ComputeChecksum(entries);
        return new InauguralDrawResult(entries, checksum);
    }

    /// <summary>
    /// Fingerprints the eight league rosters in draw order: lowercase hex SHA-256
    /// over lines of <c>ColorOrdinal:DrawIndex:Name</c> for league members ordered
    /// by sporting-color enum then draw index. Pool members do not affect the
    /// checksum. SHA-256 is a content fingerprint here, not sporting randomness.
    /// </summary>
    public static string ComputeChecksum(IReadOnlyList<InauguralDrawEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        List<InauguralDrawEntry> members = entries.Where(e => e.IsLeagueMember).ToList();
        members.Sort(static (left, right) =>
        {
            int color = left.SportingColor.CompareTo(right.SportingColor);
            return color != 0 ? color : left.DrawIndex.CompareTo(right.DrawIndex);
        });

        StringBuilder builder = new();
        foreach (InauguralDrawEntry entry in members)
        {
            builder.Append(((int)entry.SportingColor).ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.DrawIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(entry.Name);
            builder.Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static Dictionary<SportingColor, List<CatalogAthlete>> GroupByColor(IReadOnlyList<CatalogAthlete> population)
    {
        Dictionary<SportingColor, List<CatalogAthlete>> pools = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            pools[color] = [];
        }

        foreach (CatalogAthlete athlete in population)
        {
            if (athlete is null)
            {
                throw new InvalidOperationException("Save population must not contain null athletes.");
            }

            if (string.IsNullOrWhiteSpace(athlete.Name))
            {
                throw new InvalidOperationException("Save population contains an athlete with an empty name.");
            }

            pools[athlete.SportingColor].Add(athlete);
        }

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            pools[color].Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        }

        return pools;
    }

    private static void EnsureSaveQuotas(Dictionary<SportingColor, List<CatalogAthlete>> pools, RulesV1 rules)
    {
        List<string> deficits = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            int count = pools[color].Count;
            if (count != rules.AthletesPerSportingColor)
            {
                deficits.Add($"{color} has {count}, needs {rules.AthletesPerSportingColor}");
            }
        }

        if (deficits.Count > 0)
        {
            throw new InvalidOperationException(
                $"Save population cannot supply Season 1 leagues: {string.Join("; ", deficits)}.");
        }
    }
}
