namespace MtgSoloSports.Features.Reports;

/// <summary>
/// Versioned report contract shared by MSS-086 exports (athlete profile and
/// standings table) and extended by later MSS-087/MSS-088 slices.
/// JSON envelopes carry <c>schemaVersion</c>, <c>reportType</c>, <c>saveId</c>,
/// scope identifiers and snapshot phase/season/stage info plus typed data.
/// Markdown carries the same scope/status/metadata in human-readable form;
/// it never embeds JSON.
/// Sporting values stay fixed-point thousandths integers in JSON; Markdown
/// formats them to three decimals without changing sporting math.
/// </summary>
public static class ReportContract
{
    public const int SchemaVersion = 1;

    public const string AthleteReportType = "athlete-profile";

    public const string StandingsReportType = "standings-table";

    public const string JsonContentType = "application/json; charset=utf-8";

    public const string MarkdownContentType = "text/markdown; charset=utf-8";

    public static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return "json";
        }

        string normalized = format.Trim().ToLowerInvariant();
        return normalized switch
        {
            "json" => "json",
            "md" => "md",
            "markdown" => "md",
            _ => throw new ArgumentException($"Unsupported report format '{format}'. Use 'json' or 'md'.", nameof(format)),
        };
    }
}
