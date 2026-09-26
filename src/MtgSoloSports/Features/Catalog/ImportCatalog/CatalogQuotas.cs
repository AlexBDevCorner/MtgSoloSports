using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// Save-creation gate for the catalog: every sporting color must be able to
/// supply 256 unique athletes (Game Rules v1 save universe). The required
/// count mirrors <c>RulesV1.AthletesPerSportingColor</c>; MSS-006 consumes
/// this gate during universe selection. Failure aborts instead of repairing.
/// </summary>
public static class CatalogQuotas
{
    public const int RequiredPerColor = 256;

    public const int SportingColorCount = 8;

    public static IReadOnlyList<SportingColor> FindInsufficient(IReadOnlyDictionary<SportingColor, int> countsByColor)
    {
        ArgumentNullException.ThrowIfNull(countsByColor);

        List<SportingColor> insufficient = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            int count = countsByColor.TryGetValue(color, out int value) ? value : 0;
            if (count < RequiredPerColor)
            {
                insufficient.Add(color);
            }
        }

        return insufficient;
    }

    public static bool IsSufficient(IReadOnlyDictionary<SportingColor, int> countsByColor)
    {
        ArgumentNullException.ThrowIfNull(countsByColor);
        return FindInsufficient(countsByColor).Count == 0;
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when any sporting color
    /// has fewer than 256 unique athletes. Save creation must call this before
    /// selecting its 2,048-athlete universe.
    /// </summary>
    public static void EnsureSufficient(IReadOnlyDictionary<SportingColor, int> countsByColor)
    {
        ArgumentNullException.ThrowIfNull(countsByColor);

        IReadOnlyList<SportingColor> insufficient = FindInsufficient(countsByColor);
        if (insufficient.Count == 0)
        {
            return;
        }

        List<string> details = new(insufficient.Count);
        foreach (SportingColor color in insufficient)
        {
            int count = countsByColor.TryGetValue(color, out int value) ? value : 0;
            details.Add($"{color} has {count}, needs {RequiredPerColor}");
        }

        throw new InvalidOperationException(
            $"Catalog cannot supply a save universe: {string.Join("; ", details)}.");
    }
}
