using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.SimulationKernel.Leagues;

/// <summary>
/// Stable qualifier event identity: boundary plus optional sporting color.
/// Superleague qualifier carries <see cref="SportingColor"/> <c>null</c>
/// (mixed-color field); feeder boundaries carry exactly one color. Together
/// with the season transition (from/to season) this uniquely identifies one
/// persisted qualifier event. Canonical execution order is Superleague first,
/// then F1↔F2 by sporting-color enum, then F2↔F3 by sporting-color enum, so
/// equivalent state consumes RNG identically. Pure.
/// </summary>
public static class QualifierIdentity
{
    public const int SuperleagueColorSentinel = -1;

    public static readonly IReadOnlyList<(QualifierBoundary Boundary, int? Color)> CanonicalOrder = BuildCanonicalOrder();

    private static IReadOnlyList<(QualifierBoundary Boundary, int? Color)> BuildCanonicalOrder()
    {
        List<(QualifierBoundary Boundary, int? Color)> order = new(17);
        order.Add((QualifierBoundary.Superleague, null));
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            order.Add((QualifierBoundary.Feeder1Feeder2, (int)color));
        }

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            order.Add((QualifierBoundary.Feeder2Feeder3, (int)color));
        }

        return order;
    }

    public static int ToStorageColor(int? color) => color ?? SuperleagueColorSentinel;

    public static int? FromStorageColor(int stored, QualifierBoundary boundary)
    {
        if (boundary == QualifierBoundary.Superleague)
        {
            if (stored != SuperleagueColorSentinel)
            {
                throw new InvalidOperationException(
                    $"Superleague qualifier must carry color sentinel {SuperleagueColorSentinel.ToString(System.Globalization.CultureInfo.InvariantCulture)}, was {stored.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
            }

            return null;
        }

        if (stored < 0 || stored >= 8)
        {
            throw new InvalidOperationException($"Feeder qualifier has corrupt sporting color {stored.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        return stored;
    }

    public static void Validate(QualifierBoundary boundary, int? color)
    {
        if (boundary == QualifierBoundary.Superleague)
        {
            if (color.HasValue)
            {
                throw new InvalidOperationException("Superleague qualifier must not carry a sporting color.");
            }

            return;
        }

        if (!color.HasValue || color < 0 || color >= 8)
        {
            string text = color.HasValue
                ? color.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            throw new InvalidOperationException($"Feeder qualifier has corrupt sporting color {text}.");
        }
    }

    public static string Display(QualifierBoundary boundary, int? color)
    {
        return boundary switch
        {
            QualifierBoundary.Superleague => "Superleague qualifier",
            QualifierBoundary.Feeder1Feeder2 => $"F1↔F2 qualifier ({(SportingColor)color!.Value})",
            QualifierBoundary.Feeder2Feeder3 => $"F2↔F3 qualifier ({(SportingColor)color!.Value})",
            _ => throw new ArgumentOutOfRangeException(nameof(boundary), $"Unknown qualifier boundary {((int)boundary).ToString(System.Globalization.CultureInfo.InvariantCulture)}."),
        };
    }

    public static LeagueLevel HigherLevel(QualifierBoundary boundary) => boundary switch
    {
        QualifierBoundary.Superleague => LeagueLevel.Superleague,
        QualifierBoundary.Feeder1Feeder2 => LeagueLevel.Feeder1,
        QualifierBoundary.Feeder2Feeder3 => LeagueLevel.Feeder2,
        _ => throw new ArgumentOutOfRangeException(nameof(boundary), $"Unknown qualifier boundary {((int)boundary).ToString(System.Globalization.CultureInfo.InvariantCulture)}."),
    };

    public static LeagueLevel LowerLevel(QualifierBoundary boundary) => boundary switch
    {
        QualifierBoundary.Superleague => LeagueLevel.Feeder1,
        QualifierBoundary.Feeder1Feeder2 => LeagueLevel.Feeder2,
        QualifierBoundary.Feeder2Feeder3 => LeagueLevel.Feeder3,
        _ => throw new ArgumentOutOfRangeException(nameof(boundary), $"Unknown qualifier boundary {((int)boundary).ToString(System.Globalization.CultureInfo.InvariantCulture)}."),
    };
}
