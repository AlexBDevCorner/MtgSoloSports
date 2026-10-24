using System.Globalization;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Leagues.Profiles;

/// <summary>
/// Stable cross-season league identity. <see cref="Persistence.Saves.LeagueEntity.Id"/>
/// is a per-season autoincrement key, so profiles spanning seasons group by the
/// canonical sporting triple (kind, feeder division, sporting color) instead of
/// numeric ids or mutable display names. The Superleague collapses to one key;
/// every distinct feeder division/color stays a separate league. Legacy v1
/// feeder rows (division None with feeder kind) keep their own
/// <c>feeder-{color}</c> key so they never merge with tiered
/// <c>feeder-1-{color}</c> rows. Pure: no HTTP, EF Core or RNG.
/// </summary>
public static class LeagueKey
{
    public const string Superleague = "superleague";

    public static string Build(int kind, int feederDivision, int sportingColor)
    {
        if (kind == (int)Persistence.Saves.LeagueKind.Superleague)
        {
            return Superleague;
        }

        string color = ColorSlug(sportingColor);
        if (feederDivision == (int)SimulationKernel.Leagues.FeederDivision.First)
        {
            return $"feeder-1-{color}";
        }

        if (feederDivision == (int)SimulationKernel.Leagues.FeederDivision.Second)
        {
            return $"feeder-2-{color}";
        }

        if (feederDivision == (int)SimulationKernel.Leagues.FeederDivision.Third)
        {
            return $"feeder-3-{color}";
        }

        if (feederDivision == (int)SimulationKernel.Leagues.FeederDivision.None)
        {
            return $"feeder-{color}";
        }

        return $"feeder-d{feederDivision}-{color}";
    }

    public static bool TryParse(string? leagueKey, out int kind, out int? feederDivision, out int? sportingColor)
    {
        kind = (int)Persistence.Saves.LeagueKind.Feeder;
        feederDivision = null;
        sportingColor = null;
        if (string.IsNullOrWhiteSpace(leagueKey))
        {
            return false;
        }

        string normalized = leagueKey.Trim().ToLowerInvariant();
        if (string.Equals(normalized, Superleague, StringComparison.Ordinal))
        {
            kind = (int)Persistence.Saves.LeagueKind.Superleague;
            feederDivision = (int)SimulationKernel.Leagues.FeederDivision.None;
            sportingColor = null;
            return true;
        }

        if (!normalized.StartsWith("feeder-", StringComparison.Ordinal))
        {
            return false;
        }

        string rest = normalized.Substring("feeder-".Length);
        // Tiered: feeder-{1|2|3|dN}-{color}.
        int dash = rest.IndexOf('-');
        if (dash > 0)
        {
            string divisionPart = rest.Substring(0, dash);
            string colorPart = rest.Substring(dash + 1);
            int? division = ParseDivisionPart(divisionPart);
            int? color = ParseColorSlug(colorPart);
            if (division is null || color is null)
            {
                return false;
            }

            kind = (int)Persistence.Saves.LeagueKind.Feeder;
            feederDivision = division.Value;
            sportingColor = color.Value;
            return true;
        }

        // Legacy single feeder: feeder-{color} (division None).
        int? legacyColor = ParseColorSlug(rest);
        if (legacyColor is null)
        {
            return false;
        }

        kind = (int)Persistence.Saves.LeagueKind.Feeder;
        feederDivision = (int)SimulationKernel.Leagues.FeederDivision.None;
        sportingColor = legacyColor.Value;
        return true;
    }

    internal static string ColorSlug(int sportingColor)
    {
        if (Enum.IsDefined(typeof(SportingColor), sportingColor))
        {
            return ((SportingColor)sportingColor).ToString().ToLowerInvariant();
        }

        return $"color{sportingColor}";
    }

    private static int? ParseDivisionPart(string part)
    {
        if (string.Equals(part, "1", StringComparison.Ordinal))
        {
            return (int)SimulationKernel.Leagues.FeederDivision.First;
        }

        if (string.Equals(part, "2", StringComparison.Ordinal))
        {
            return (int)SimulationKernel.Leagues.FeederDivision.Second;
        }

        if (string.Equals(part, "3", StringComparison.Ordinal))
        {
            return (int)SimulationKernel.Leagues.FeederDivision.Third;
        }

        if (part.StartsWith("d", StringComparison.Ordinal)
            && int.TryParse(part.Substring(1), CultureInfo.InvariantCulture, out int raw)
            && raw >= 0)
        {
            return raw;
        }

        return null;
    }

    private static int? ParseColorSlug(string slug)
    {
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            if (string.Equals(color.ToString(), slug, StringComparison.OrdinalIgnoreCase))
            {
                return (int)color;
            }
        }

        return null;
    }
}
