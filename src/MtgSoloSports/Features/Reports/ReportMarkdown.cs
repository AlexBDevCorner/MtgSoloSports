using System.Globalization;

namespace MtgSoloSports.Features.Reports;

/// <summary>
/// Compact Markdown primitives shared by report slices. Display-only: formats
/// fixed-point thousandths without changing sporting math.
/// </summary>
public static class ReportMarkdown
{
    public static string FormatPoints(int thousandths)
    {
        return (thousandths / 1000.0).ToString("F3", CultureInfo.InvariantCulture);
    }

    public static string FormatBonus(int thousandths)
    {
        string sign = thousandths >= 0 ? "+" : string.Empty;
        return $"{sign}{(thousandths / 1000.0).ToString("F3", CultureInfo.InvariantCulture)}%";
    }

    public static string RankOrDash(int? rank)
    {
        return rank.HasValue ? $"P{rank.Value}" : "— (not ranked)";
    }

    public static string TextOrDash(string? value, string emptyLabel = "— (none)")
    {
        return string.IsNullOrWhiteSpace(value) ? emptyLabel : value;
    }

    /// <summary>
    /// Escapes Markdown table pipes/newlines in free-text names so tables stay
    /// one row per record. Sporting values never pass through here.
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "—";
        }

        return value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
