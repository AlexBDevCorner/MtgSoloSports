using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Leagues.InauguralDraw;

/// <summary>
/// Structural invariants for Season 1 leagues and common pools. Fundamental
/// failures throw and abort the mutation; corrupted sporting state is never
/// silently repaired. Version-aware: v1 expects 32 active / 224 pool per color
/// (8 leagues, 256 active globally); v2 tiered expects 96 active / 160 pool
/// per color (24 leagues F1/F2/F3, 768 active globally) with divisions derived
/// from draw index (0..31 F1, 32..63 F2, 64..95 F3, 96..255 pool).
/// </summary>
public static class Season1Invariants
{
    /// <summary>
    /// Validates an in-memory draw result: exact total, exact per-color league
    /// and pool counts, unique draw indices per color, no duplicate athletes and
    /// league membership matching the first <c>Season1ActivePerColor</c> draw positions.
    /// </summary>
    public static void ValidateDrawResult(IReadOnlyList<InauguralDrawEntry> entries, RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(rules);
        EnsureDrawTotal(entries, rules);
        DrawCounts counts = AccumulateDrawEntries(entries, rules);
        EnsureDrawPerColor(counts, rules);
        EnsureDrawActiveTotal(counts, rules);
    }

    private static void EnsureDrawTotal(IReadOnlyList<InauguralDrawEntry> entries, RulesV1 rules)
    {
        if (entries.Count != rules.TotalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Season 1 draw must contain exactly {rules.TotalAthletesInSave} athletes, was {entries.Count}.");
        }
    }

    private sealed record DrawCounts(
        Dictionary<SportingColor, HashSet<int>> IndicesPerColor,
        Dictionary<SportingColor, int> LeaguePerColor,
        Dictionary<SportingColor, int> PoolPerColor);

    private static DrawCounts AccumulateDrawEntries(IReadOnlyList<InauguralDrawEntry> entries, RulesV1 rules)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        Dictionary<SportingColor, HashSet<int>> indices = NewColorMap(static () => new HashSet<int>());
        Dictionary<SportingColor, int> league = NewColorMap(static () => 0);
        Dictionary<SportingColor, int> pool = NewColorMap(static () => 0);

        foreach (InauguralDrawEntry entry in entries)
        {
            CheckDrawEntry(entry, names, indices, rules);
            if (entry.IsLeagueMember)
            {
                league[entry.SportingColor]++;
            }
            else
            {
                pool[entry.SportingColor]++;
            }
        }

        return new DrawCounts(indices, league, pool);
    }

    private static void CheckDrawEntry(
        InauguralDrawEntry entry,
        HashSet<string> names,
        Dictionary<SportingColor, HashSet<int>> indices,
        RulesV1 rules)
    {
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidOperationException("Season 1 draw contains an athlete with an empty name.");
        }

        if (!names.Add(entry.Name))
        {
            throw new InvalidOperationException($"Season 1 draw contains duplicate athlete '{entry.Name}'.");
        }

        if (entry.DrawIndex < 0 || entry.DrawIndex >= rules.AthletesPerSportingColor)
        {
            throw new InvalidOperationException(
                $"Season 1 draw index for '{entry.Name}' must be in [0, {rules.AthletesPerSportingColor}), was {entry.DrawIndex}.");
        }

        if (!indices[entry.SportingColor].Add(entry.DrawIndex))
        {
            throw new InvalidOperationException(
                $"Season 1 draw contains duplicate draw index {entry.DrawIndex} for {entry.SportingColor}.");
        }

        bool expectedMember = entry.DrawIndex < rules.Season1ActivePerColor;
        if (entry.IsLeagueMember != expectedMember)
        {
            throw new InvalidOperationException(
                $"Season 1 draw membership for '{entry.Name}' contradicts draw index {entry.DrawIndex}.");
        }
    }

    private static void EnsureDrawPerColor(DrawCounts counts, RulesV1 rules)
    {
        int expectedLeague = rules.Season1ActivePerColor;
        int expectedPool = rules.Season1PoolPerColor;
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            if (counts.LeaguePerColor[color] != expectedLeague)
            {
                throw new InvalidOperationException(
                    $"Season 1 league {color} must contain exactly {expectedLeague} athletes, was {counts.LeaguePerColor[color]}.");
            }

            if (counts.PoolPerColor[color] != expectedPool)
            {
                throw new InvalidOperationException(
                    $"Season 1 pool {color} must contain exactly {expectedPool} athletes, was {counts.PoolPerColor[color]}.");
            }

            if (counts.IndicesPerColor[color].Count != rules.AthletesPerSportingColor)
            {
                throw new InvalidOperationException(
                    $"Season 1 draw for {color} must contain {rules.AthletesPerSportingColor} distinct draw indices, was {counts.IndicesPerColor[color].Count}.");
            }
        }
    }

    private static void EnsureDrawActiveTotal(DrawCounts counts, RulesV1 rules)
    {
        int active = counts.LeaguePerColor.Values.Sum();
        int expected = rules.Season1ActiveTotal;
        if (active != expected)
        {
            throw new InvalidOperationException(
                $"Season 1 must have exactly {expected} active athletes, was {active}.");
        }
    }

    private static Dictionary<SportingColor, T> NewColorMap<T>(Func<T> factory)
    {
        Dictionary<SportingColor, T> map = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            map[color] = factory();
        }

        return map;
    }
}
