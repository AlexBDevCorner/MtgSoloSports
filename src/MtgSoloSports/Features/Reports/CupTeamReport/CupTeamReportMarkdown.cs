using System.Globalization;
using System.Text;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.CupTeamReport;

/// <summary>
/// Markdown rendering for Cup team reports. Display-only; never resimulates.
/// </summary>
internal static class CupTeamReportMarkdown
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetCupTeamReportHandler.ReportData data)
    {
        StringBuilder sb = new();
        AppendHeader(sb, saveId, metadata, rules, seasonCount, data);
        AppendHonours(sb, data.History);
        AppendSeasons(sb, data.History);
        AppendRoster(sb, data.History);
        AppendIndividualMedals(sb, data.History);
        return sb.ToString();
    }

    private static void AppendHeader(
        StringBuilder sb,
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetCupTeamReportHandler.ReportData data)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {data.Cup} Cup team report — {data.History.TeamName}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Save: {metadata.Name} (`{saveId:D}`) · Season {metadata.CurrentSeason} · Phase {metadata.Phase}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Scope: {data.Cup} Cup team `{ReportMarkdown.Cell(data.History.TeamKey)}` (historical affiliations only; never the current league team)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Report: {ReportContract.CupTeamReportType} v{ReportContract.SchemaVersion} · Rules v{rules.Version} · Schema v{metadata.SchemaVersion}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Snapshot: {seasonCount} season(s) in save · Editions: {data.History.Honours.Editions} · Roster: {data.History.Roster.Count} athletes.");
        sb.AppendLine("- Units: scores are fixed-point thousandths (divide by 1000). `—` means unavailable; 0 is an exact zero.");
        sb.AppendLine("- Type Cup qualification-only rows keep the appearance without honours; only Final ranks 1–3 hold titles.");
        sb.AppendLine();
    }

    private static void AppendHonours(StringBuilder sb, CupTeamHistoryResponse history)
    {
        sb.AppendLine("## Honours");
        sb.AppendLine();
        string best = history.Honours.BestRank.HasValue && history.Honours.BestRankSeasonNumber.HasValue
            ? $"P{history.Honours.BestRank.Value} (Season {history.Honours.BestRankSeasonNumber.Value})"
            : "— (no finished Cup yet)";
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Editions: {history.Honours.Editions} · Gold {history.Honours.Gold} · Silver {history.Honours.Silver} · Bronze {history.Honours.Bronze} · Best {best}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Group wins: {history.Honours.GroupWins} · Round wins: {history.Honours.RoundWins} · Total score: {ReportMarkdown.FormatPoints(checked((int)Math.Clamp(history.Honours.TotalScoreThousandths, int.MinValue, int.MaxValue)))} ({history.Honours.TotalScoreThousandths} thousandths)");
        sb.AppendLine();
    }

    private static void AppendSeasons(StringBuilder sb, CupTeamHistoryResponse history)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Seasons ({history.Seasons.Count})");
        sb.AppendLine();
        if (history.Seasons.Count == 0)
        {
            sb.AppendLine("No editions yet for this team.");
            sb.AppendLine();
            return;
        }

        foreach (CupTeamHistoryResponse.Season season in history.Seasons.OrderByDescending(s => s.SourceSeasonNumber))
        {
            string stage = string.IsNullOrWhiteSpace(season.TournamentStage) ? string.Empty : $" · {season.TournamentStage}";
            string fate = season.EliminatedInQualification ? " · Eliminated in qualification"
                : season.QualifiedForFinal ? " · Qualified for Final" : string.Empty;
            string result = season.TeamRank.HasValue
                ? $"P{season.TeamRank.Value} of {season.TeamCount}{stage}{fate} · {ReportMarkdown.Cell(season.Medal)} · {ReportMarkdown.FormatPoints(season.TeamScoreThousandths ?? 0)}"
                : $"{season.State} — no result yet{stage}{fate}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"### Season {season.SourceSeasonNumber} — {result}");
            sb.AppendLine();
            sb.AppendLine("| # | Card | Leg | Rating | Reason |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (CupTeamHistoryResponse.SquadMember member in season.Squad.OrderBy(m => m.SelectionRank))
            {
                string leg = member.Leg is null
                    ? "Leg not played yet"
                    : $"Group {member.Leg.GroupNumber} P{member.Leg.GroupRank} · {ReportMarkdown.FormatPoints(member.Leg.GroupScoreThousandths)}";
                string individual = member.Individual is null
                    ? string.Empty
                    : $" · Individual P{member.Individual.CupRank} {member.Individual.Medal}";
                sb.AppendLine(CultureInfo.InvariantCulture, $"| #{member.SelectionRank} | {ReportMarkdown.Cell(member.Name)} (id {member.AthleteId}) | {ReportMarkdown.Cell(leg + individual)} | {ReportMarkdown.FormatPoints(member.FinalRatingThousandths)} | {ReportMarkdown.Cell(member.Reason ?? "—")} |");
            }

            sb.AppendLine();
        }
    }

    private static void AppendRoster(StringBuilder sb, CupTeamHistoryResponse history)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## All-time roster ({history.Roster.Count})");
        sb.AppendLine();
        if (history.Roster.Count == 0)
        {
            sb.AppendLine("No roster rows.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Card | Caps | Seasons | Leg score | Best leg |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (CupTeamHistoryResponse.RosterEntry entry in history.Roster)
        {
            string seasons = entry.FirstSeasonNumber == entry.LastSeasonNumber
                ? $"S{entry.FirstSeasonNumber}"
                : $"S{entry.FirstSeasonNumber}–S{entry.LastSeasonNumber}";
            string best = entry.BestGroupRank.HasValue ? $"P{entry.BestGroupRank.Value}" : "—";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {ReportMarkdown.Cell(entry.Name)} (id {entry.AthleteId}) | {entry.Caps} | {seasons} | {ReportMarkdown.FormatPoints(checked((int)Math.Clamp(entry.TotalLegScoreThousandths, int.MinValue, int.MaxValue)))} | {best} |");
        }

        sb.AppendLine();
    }

    private static void AppendIndividualMedals(StringBuilder sb, CupTeamHistoryResponse history)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Individual medals ({history.IndividualMedals.Count}, Color Cup only)");
        sb.AppendLine();
        if (history.IndividualMedals.Count == 0)
        {
            sb.AppendLine("No individual Color Cup medals for this team.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Season | Card | Medal |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (CupTeamHistoryResponse.IndividualMedal medal in history.IndividualMedals)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {medal.SourceSeasonNumber} | {ReportMarkdown.Cell(medal.Name)} (id {medal.AthleteId}) | {ReportMarkdown.Cell(medal.Medal)} |");
        }

        sb.AppendLine();
    }
}
