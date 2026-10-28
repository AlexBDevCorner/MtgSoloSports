namespace MtgSoloSports.Features.Reports;

/// <summary>
/// Safe ASCII filenames for report downloads. Slugs strip everything except
/// letters/digits (other runs become one '-'), collapse, trim and truncate so
/// Content-Disposition never carries path separators or control characters.
/// Full UTF-8 names stay inside the report body; the filename keeps ids plus
/// a short slug so downloads stay distinguishable.
/// </summary>
public static class ReportFilenames
{
    public static string Slug(string? value, int maxLength = 40)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "report";
        }

        System.Text.StringBuilder builder = new();
        bool lastDash = true;
        foreach (char ch in value.Trim())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9'))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastDash = false;
            }
            else if (!lastDash)
            {
                builder.Append('-');
                lastDash = true;
            }

            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        string slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "report" : slug;
    }

    public static string AthleteFileName(int athleteId, string? athleteName, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        string ext = extension.TrimStart('.').ToLowerInvariant();
        return $"athlete-{athleteId}-{Slug(athleteName)}.{ext}";
    }

    public static string StandingsFileName(int seasonNumber, int leagueId, string? leagueName, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        string ext = extension.TrimStart('.').ToLowerInvariant();
        return $"standings-season-{seasonNumber}-league-{leagueId}-{Slug(leagueName)}.{ext}";
    }
}
