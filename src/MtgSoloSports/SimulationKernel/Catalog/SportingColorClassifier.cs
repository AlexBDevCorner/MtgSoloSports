namespace MtgSoloSports.SimulationKernel.Catalog;

/// <summary>
/// Pure deterministic sporting-color classification from Game Rules v1.
/// Precedence: Devoid -&gt; Colorless, then Hybrid mana -&gt; Hybrid (over Multicolor),
/// then printed front-face color count (0 -&gt; Colorless, 1 -&gt; mono, 2+ -&gt; Multicolor).
/// Colored artifact creatures follow their printed color; colorless artifact
/// creatures are Colorless via the zero-color rule. Double-faced cards are
/// classified by the caller from front-face inputs only.
/// No I/O, clock, randomness or EF dependencies.
/// </summary>
public static class SportingColorClassifier
{
    private static readonly IReadOnlyDictionary<string, SportingColor> MonoMap =
        new Dictionary<string, SportingColor>(StringComparer.Ordinal)
        {
            ["W"] = SportingColor.White,
            ["U"] = SportingColor.Blue,
            ["B"] = SportingColor.Black,
            ["R"] = SportingColor.Red,
            ["G"] = SportingColor.Green,
        };

    /// <summary>
    /// Classifies one athlete candidate from front-face printed color plus flags.
    /// </summary>
    /// <param name="frontColors">Front-face printed colors, each one of W/U/B/R/G. Duplicates are ignored.</param>
    /// <param name="hasHybridMana">True when the front-face mana cost contains a hybrid symbol.</param>
    /// <param name="hasDevoid">True when the front face has Devoid.</param>
    public static SportingColor Classify(
        IReadOnlyList<string> frontColors,
        bool hasHybridMana,
        bool hasDevoid)
    {
        ArgumentNullException.ThrowIfNull(frontColors);

        if (hasDevoid)
        {
            return SportingColor.Colorless;
        }

        if (hasHybridMana)
        {
            return SportingColor.Hybrid;
        }

        HashSet<string> distinct = new(StringComparer.Ordinal);
        foreach (string color in frontColors)
        {
            if (string.IsNullOrWhiteSpace(color))
            {
                throw new InvalidOperationException("Front-face color entries must not be empty.");
            }

            string normalized = color.Trim().ToUpperInvariant();
            if (!MonoMap.ContainsKey(normalized))
            {
                throw new InvalidOperationException($"Unknown front-face color '{color}'. Expected one of W/U/B/R/G.");
            }

            _ = distinct.Add(normalized);
        }

        if (distinct.Count == 0)
        {
            return SportingColor.Colorless;
        }

        if (distinct.Count == 1)
        {
            foreach (string single in distinct)
            {
                return MonoMap[single];
            }
        }

        return SportingColor.Multicolor;
    }

    /// <summary>
    /// Normalizes front-face colors to distinct uppercase WUBRG letters in canonical WUBRG order.
    /// </summary>
    public static IReadOnlyList<string> NormalizeColors(IReadOnlyList<string> frontColors)
    {
        ArgumentNullException.ThrowIfNull(frontColors);

        HashSet<string> distinct = new(StringComparer.Ordinal);
        foreach (string color in frontColors)
        {
            if (string.IsNullOrWhiteSpace(color))
            {
                throw new InvalidOperationException("Front-face color entries must not be empty.");
            }

            string normalized = color.Trim().ToUpperInvariant();
            if (!MonoMap.ContainsKey(normalized))
            {
                throw new InvalidOperationException($"Unknown front-face color '{color}'. Expected one of W/U/B/R/G.");
            }

            _ = distinct.Add(normalized);
        }

        string[] order = ["W", "U", "B", "R", "G"];
        List<string> result = [];
        foreach (string slot in order)
        {
            if (distinct.Contains(slot))
            {
                result.Add(slot);
            }
        }

        return result;
    }
}
