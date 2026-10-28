using System.Globalization;
using System.Text;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;

namespace MtgSoloSports.Features.Reports.StandingsReport;

/// <summary>
/// Markdown rendering for standings reports. Display-only; never resimulates.
/// </summary>
internal static class StandingsReportMarkdown
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        SeasonEntity season,
        LeagueEntity league,
        GetStandingsReportHandler.ReportData data)
    {
        StringBuilder sb = new();
        AppendHeader(sb, saveId, metadata, rules, season, league, data);
        AppendTable(sb, data);
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, Guid saveId, SaveMetadataEntity metadata, SimulationKernel.Rules.RulesV1 rules, SeasonEntity season, LeagueEntity league, GetStandingsReportHandler.ReportData data)
    {
        LeagueLevel level = LeagueEntityLevels.GetLevel(league);
        FeederDivision division = (FeederDivision)league.FeederDivision;
        string kind = ((LeagueKind)league.Kind).ToString();
        string colorName = Enum.IsDefined(typeof(SimulationKernel.Catalog.SportingColor), league.SportingColor)
            ? ((SimulationKernel.Catalog.SportingColor)league.SportingColor).ToString()
            : "Unknown";
        string statusLabel = data.IsFinal ? "Complete · final table" : "In progress · provisional table";
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Standings report — {league.Name} · Season {season.SeasonNumber}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Save: {metadata.Name} (`{saveId:D}`) · Season {season.SeasonNumber} · League {league.Id} ({league.Name})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Scope: season {season.SeasonNumber}, league {league.Id} · Snapshot: {statusLabel}, {data.CompletedStages}/{data.TotalStages} stages, global stage {data.GlobalStage}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Report: {ReportContract.StandingsReportType} v{ReportContract.SchemaVersion} · Rules v{rules.Version} · Schema v{metadata.SchemaVersion}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"League: {league.Name} · Kind {kind} · Tier {level} · Division {league.FeederDivision} · Color {colorName}");
        string checksum = string.IsNullOrEmpty(data.SeasonChecksum) ? string.Empty : $" · Checksum `{data.SeasonChecksum}`";
        sb.AppendLine(CultureInfo.InvariantCulture, $"Status: {statusLabel} (isFinal={data.IsFinal.ToString().ToLowerInvariant()}){checksum}");
        string zoneNote = data.IsFinal ? " (authoritative final zones)." : " (provisional: zones omitted, not authoritative).";
        sb.AppendLine(CultureInfo.InvariantCulture, $"Zones: {ReportZones.ZoneSummary(level, division)}{zoneNote}");
        sb.AppendLine();
        sb.AppendLine("Units: championship points are official season totals in fixed-point thousandths (divide by 1000, e.g. 77000 = 77.000; 32-position table, no bonus multiplier). Stage/base scores are persisted display sums in the same thousandths. — means unavailable; 0 is an exact zero. Only rank 1 of a final table is the champion.");
        sb.AppendLine();
    }

    private static void AppendTable(StringBuilder sb, GetStandingsReportHandler.ReportData data)
    {
        if (data.Rows.Count == 0)
        {
            sb.AppendLine("No standings yet. Complete a stage to populate championship standings; opening this page never requires finishing the running stage.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"## Table ({data.Rows.Count} rows, rank order)");
        sb.AppendLine();
        if (data.IsFinal)
        {
            sb.AppendLine("| Rank | Athlete | Championship pts | Stage W | Round W | Zone |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
            foreach (GetStandingsReportHandler.StandingsReportRow row in data.Rows.OrderBy(r => r.Rank))
            {
                string champion = row.IsChampion ? " · Champion" : string.Empty;
                string zone = row.ZoneLabel ?? "—";
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.Rank}{champion} | {ReportMarkdown.Cell(row.Name)} (id {row.AthleteId}) | {ReportMarkdown.FormatPoints(row.TotalChampionshipPointsThousandths)} | {row.StageWins} | {row.RoundWins} | {ReportMarkdown.Cell(zone)} |");
            }
        }
        else
        {
            sb.AppendLine("| Rank | Athlete | Championship pts | Stage W | Round W |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (GetStandingsReportHandler.StandingsReportRow row in data.Rows.OrderBy(r => r.Rank))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.Rank} | {ReportMarkdown.Cell(row.Name)} (id {row.AthleteId}) | {ReportMarkdown.FormatPoints(row.TotalChampionshipPointsThousandths)} | {row.StageWins} | {row.RoundWins} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine(data.IsFinal
            ? "Persisted final table; completed seasons never change when new stages run."
            : "Live cumulative table from persisted completed stages only; an incomplete stage never contributes points. Final ranks persist at season end.");
        sb.AppendLine();
    }
}
