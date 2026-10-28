using System.Globalization;
using System.Text;
using MtgSoloSports.Features.Leagues.Profiles;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.LeagueProfileReport;

/// <summary>
/// Markdown rendering for league profile reports. Display-only; never resimulates.
/// </summary>
internal static class LeagueProfileReportMarkdown
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetLeagueProfileReportHandler.ReportData data)
    {
        StringBuilder sb = new();
        AppendHeader(sb, saveId, metadata, rules, seasonCount, data.Profile);
        AppendCurrent(sb, data.Profile);
        AppendPodiums(sb, data.Profile);
        AppendLeaderboard(sb, data.Profile);
        AppendRecords(sb, data.Profile);
        return sb.ToString();
    }

    private static void AppendHeader(
        StringBuilder sb,
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetLeagueProfileResponse profile)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"# League profile report — {profile.Name}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Save: {metadata.Name} (`{saveId:D}`) · Season {metadata.CurrentSeason} · Phase {metadata.Phase}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Scope: league `{ReportMarkdown.Cell(profile.LeagueKey)}` (stable identity; never substituted with another league)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Report: {ReportContract.LeagueProfileReportType} v{ReportContract.SchemaVersion} · Rules v{rules.Version} · Schema v{metadata.SchemaVersion}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- League: {ReportMarkdown.Cell(profile.Name)} · Kind {ReportMarkdown.Cell(profile.Kind)} · Level {ReportMarkdown.Cell(profile.LeagueLevel)} · Color {ReportMarkdown.Cell(profile.SportingColorName)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Snapshot: {seasonCount} season(s) in save · Completed in this league: {profile.CompletedSeasons} · Athletes: {profile.UniqueAthletes} · Span {(profile.FirstSeasonNumber?.ToString(CultureInfo.InvariantCulture) ?? "—")}–{(profile.LastCompletedSeasonNumber?.ToString(CultureInfo.InvariantCulture) ?? "—")}");
        sb.AppendLine("- Units: Total league points are official final season championship points summed while competing here (thousandths, divide by 1000). Weighted Hall of Fame career points never contribute. `—` means unavailable; 0 is an exact zero.");
        sb.AppendLine();
    }

    private static void AppendCurrent(StringBuilder sb, GetLeagueProfileResponse profile)
    {
        sb.AppendLine("## Current provisional season — not yet final");
        sb.AppendLine();
        if (profile.CurrentSeason is null)
        {
            sb.AppendLine("No provisional season: the latest season of this league is complete.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"- Season {profile.CurrentSeason.SeasonNumber} (league id {profile.CurrentSeason.LeagueId}) is still in progress and contributes no medals, records or all-time points. Not yet final.");
        sb.AppendLine();
    }

    private static void AppendPodiums(StringBuilder sb, GetLeagueProfileResponse profile)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Podium history ({profile.PodiumHistory.Count} completed seasons, newest first)");
        sb.AppendLine();
        if (profile.PodiumHistory.Count == 0)
        {
            sb.AppendLine("No completed seasons yet.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Season | Champion | Runner-up | Third | Margin |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (GetLeagueProfileResponse.PodiumSeason season in profile.PodiumHistory)
        {
            string margin = season.WinningMarginThousandths.HasValue
                ? ReportMarkdown.FormatPoints(season.WinningMarginThousandths.Value)
                : "—";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {season.SeasonNumber} | {PodiumCell(season.Champion)} | {PodiumCell(season.RunnerUp)} | {PodiumCell(season.Third)} | {margin} |");
        }

        sb.AppendLine();
    }

    private static string PodiumCell(GetLeagueProfileResponse.PodiumAthlete? athlete)
    {
        if (athlete is null)
        {
            return "—";
        }

        return $"{ReportMarkdown.Cell(athlete.AthleteName)} (id {athlete.AthleteId}) · {ReportMarkdown.FormatPoints(athlete.TotalChampionshipPointsThousandths)}";
    }

    private static void AppendLeaderboard(StringBuilder sb, GetLeagueProfileResponse profile)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## All-time Total league points ({profile.AllTimeLeaderboard.Count} athletes)");
        sb.AppendLine();
        if (profile.AllTimeLeaderboard.Count == 0)
        {
            sb.AppendLine("No all-time points yet.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Rank | Card | Total league points | Seasons | Best | Titles | Podiums |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        int rank = 0;
        foreach (GetLeagueProfileResponse.AllTimeRow row in profile.AllTimeLeaderboard)
        {
            rank++;
            string best = row.BestRank == 0 ? "—" : $"P{row.BestRank}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {rank} | {ReportMarkdown.Cell(row.AthleteName)} (id {row.AthleteId}) | {ReportMarkdown.FormatPoints(checked((int)Math.Clamp(row.TotalLeaguePointsThousandths, int.MinValue, int.MaxValue)))} ({row.TotalLeaguePointsThousandths} thousandths) | {row.CompletedSeasons} | {best} | {row.Titles} | {row.Podiums} |");
        }

        sb.AppendLine();
    }

    private static void AppendRecords(StringBuilder sb, GetLeagueProfileResponse profile)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## League records ({profile.Records.Count})");
        sb.AppendLine();
        if (profile.Records.Count == 0)
        {
            sb.AppendLine("No records yet.");
            sb.AppendLine();
            return;
        }

        foreach (GetLeagueProfileResponse.LeagueRecordEntry record in profile.Records)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {ReportMarkdown.Cell(record.Label)} (`{ReportMarkdown.Cell(record.RecordKey)}`) — {ReportMarkdown.Cell(record.ValueDisplay)}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(record.ScopeNote))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"_{ReportMarkdown.Cell(record.ScopeNote)}_");
                sb.AppendLine();
            }

            foreach (GetLeagueProfileResponse.LeagueRecordHolder holder in record.Holders)
            {
                string seasons = holder.SeasonNumbers.Count switch
                {
                    0 => string.Empty,
                    1 => $" (Season {holder.SeasonNumbers[0]})",
                    _ => $" (Seasons {string.Join(", ", holder.SeasonNumbers)})",
                };
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {ReportMarkdown.Cell(holder.AthleteName)} (id {holder.AthleteId}){ReportMarkdown.Cell(seasons)}");
            }

            sb.AppendLine();
        }
    }
}
