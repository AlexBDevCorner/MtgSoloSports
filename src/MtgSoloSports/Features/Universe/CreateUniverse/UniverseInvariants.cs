using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Universe.CreateUniverse;

/// <summary>
/// Structural invariants for the save universe. Fundamental failures throw
/// and abort the mutation; corrupted sporting state is never silently repaired.
/// </summary>
public static class UniverseInvariants
{
    /// <summary>
    /// Validates a selected snapshot against the save's rules quotas: exact
    /// total, exact per-color counts and no duplicate athlete names.
    /// </summary>
    public static void ValidateSelection(
        IReadOnlyList<CatalogAthlete> selected,
        int athletesPerSportingColor,
        int totalAthletesInSave)
    {
        ArgumentNullException.ThrowIfNull(selected);

        if (athletesPerSportingColor <= 0)
        {
            throw new InvalidOperationException($"Athletes per sporting color must be positive, was {athletesPerSportingColor}.");
        }

        if (totalAthletesInSave <= 0)
        {
            throw new InvalidOperationException($"Total athletes in save must be positive, was {totalAthletesInSave}.");
        }

        if (selected.Count != totalAthletesInSave)
        {
            throw new InvalidOperationException(
                $"Save universe must contain exactly {totalAthletesInSave} athletes, was {selected.Count}.");
        }

        Dictionary<SportingColor, int> counts = new();
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (CatalogAthlete athlete in selected)
        {
            if (string.IsNullOrWhiteSpace(athlete.Name))
            {
                throw new InvalidOperationException("Save universe contains an athlete with an empty name.");
            }

            if (!names.Add(athlete.Name))
            {
                throw new InvalidOperationException($"Save universe contains duplicate athlete '{athlete.Name}'.");
            }

            counts.TryGetValue(athlete.SportingColor, out int count);
            counts[athlete.SportingColor] = count + 1;
        }

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            int count = counts.TryGetValue(color, out int value) ? value : 0;
            if (count != athletesPerSportingColor)
            {
                throw new InvalidOperationException(
                    $"Save universe must contain exactly {athletesPerSportingColor} {color} athletes, was {count}.");
            }
        }
    }
}
