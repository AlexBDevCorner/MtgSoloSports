using System.Globalization;
using System.Text;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.CupEditionReport;

/// <summary>
/// Markdown rendering for the Chaos Cup dossier (MSS-114). Display-only; never
/// resimulates. Every entrant appears exactly once in the paths table; only the
/// completed Final carries official places P1..P32.
/// </summary>
internal static class ChaosDossierMarkdown
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(data);

        StringBuilder sb = new();
        AppendHeader(sb, saveId, metadata, rules, seasonCount, data);
        AppendBreakdown(sb, data);
        AppendStages(sb, data);
        AppendFinal(sb, data);
        AppendPaths(sb, data);
        return sb.ToString();
    }

    private static void AppendHeader(
        StringBuilder sb,
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(data);

        sb.AppendLine(CultureInfo.InvariantCulture, $"# Chaos Cup edition dossier — Season {data.SourceSeasonNumber}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Save: {metadata.Name} (`{saveId:D}`) · Season {metadata.CurrentSeason} · Phase {metadata.Phase}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Scope: Chaos Cup, source season {data.SourceSeasonNumber} (never substituted with another edition)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Report: {ReportContract.ChaosCupDossierReportType} v{ReportContract.SchemaVersion} · Rules v{data.RulesVersion} (save v{rules.Version}) · Chaos format v{data.ChaosFormatVersion} · Schema v{metadata.SchemaVersion}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Edition state: {data.State} · Field {data.FieldSize} athletes · {data.RoundsPlayed}/{data.TotalRounds} rounds · {data.GroupsCompleted}/{data.TotalGroups} groups");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Snapshot: {seasonCount} season(s) in save; this report reads exactly source season {data.SourceSeasonNumber}.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Checksum: `{data.EditionChecksum}` · Stages {data.StageCount} (32/16/8/4/2/1 groups of 32, 16 rounds each).");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Units: Chaos group scores are fixed-point thousandths (divide by 1000). `—` means unavailable; 0 is an exact zero.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Chaos group points are isolated per group and reset every stage; they award no season championship points, promotion, or new bonus.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Chaos group points are separate from additive Career Leaders points and Superleague points leaders; do not substitute one for another.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Eliminated athletes carry only an exit-stage category; no global numeric rank is fabricated. Only the completed Final holds official places P1..P32.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Entrants and paths are ordered by athlete id ascending for determinism; that order is not a ranking.");
        if (data.Champion is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Champion: {ReportMarkdown.Cell(data.Champion.Name)} (id {data.Champion.AthleteId}) · Final {ReportMarkdown.FormatPoints(data.Champion.FinalScoreThousandths)} · {data.Champion.RoundWins} round wins.");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Champion: pending — the Final has not finished yet; no official winner.");
        }

        sb.AppendLine();
    }

    private static void AppendBreakdown(StringBuilder sb, ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(data);

        sb.AppendLine(CultureInfo.InvariantCulture, $"## Field ({data.FieldSize} entrants)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Frozen source-season athletes plus Pool invitations (128 per sporting color). Source tier is frozen before promotion/rebalancing.");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Color | Entrants |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- |");
        foreach (ChaosDossierData.Breakdown bucket in data.ByColor)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {ReportMarkdown.Cell(bucket.Label)} | {bucket.Count} |");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Division | Entrants |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- |");
        foreach (ChaosDossierData.Breakdown bucket in data.ByDivision)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {ReportMarkdown.Cell(bucket.Label)} | {bucket.Count} |");
        }

        sb.AppendLine();
    }

    private static void AppendStages(StringBuilder sb, ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(data);

        sb.AppendLine(CultureInfo.InvariantCulture, $"## Stages (six group stages)");
        sb.AppendLine();
        foreach (ChaosDossierData.Stage stage in data.Stages.OrderBy(s => s.StageIndex))
        {
            AppendStage(sb, stage);
        }
    }

    private static void AppendStage(StringBuilder sb, ChaosDossierData.Stage stage)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(stage);

        string title = stage.IsFinal
            ? "### Final — 32 athletes · 1 group"
            : $"### Stage {stage.StageIndex} — {stage.EntrantCount} athletes · {stage.GroupCount} groups";
        sb.AppendLine(title);
        sb.AppendLine();
        string checksum = string.IsNullOrWhiteSpace(stage.DrawChecksum) ? "not drawn" : $"`{stage.DrawChecksum}`";
        sb.AppendLine(CultureInfo.InvariantCulture, $"- State: {stage.State} · Groups {stage.GroupsCompleted}/{stage.GroupCount} · Rounds {stage.RoundsPlayed}/{stage.TotalRounds} · Draw {checksum}.");
        if (stage.StageIndex == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- First draw: exactly 4 per sporting color in every group.");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Later draws: pure random, no color or tier seeding.");
        }
        sb.AppendLine();
        if (!stage.IsDrawn)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Draws once every group of the previous stage completes.");
            sb.AppendLine();
            return;
        }

        foreach (ChaosDossierData.Group group in stage.Groups.OrderBy(g => g.GroupNumber))
        {
            AppendGroup(sb, stage, group);
        }
    }

    private static void AppendGroup(StringBuilder sb, ChaosDossierData.Stage stage, ChaosDossierData.Group group)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(group);

        string name = stage.IsFinal ? "Final" : $"Group {group.GroupNumber}";
        if (!group.IsComplete)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"#### {name} — in progress ({group.RoundsPlayed}/{group.TotalRounds} rounds, provisional)");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Standings provisional: group has not completed all {group.TotalRounds} rounds, so no official ranks or cutoff are listed.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"#### {name} — official ({group.RoundsPlayed}/{group.TotalRounds} rounds)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Rank | Card | Group score | Base | Round W | Adv |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- | --- | --- | --- | --- |");
        foreach (ChaosDossierData.Standing row in group.Standings.OrderBy(r => r.GroupRank))
        {
            string adv = stage.IsFinal ? "—" : row.IsAdvanced ? "yes" : "no";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.GroupRank} | {ReportMarkdown.Cell(row.Name)} (id {row.AthleteId}) | {ReportMarkdown.FormatPoints(row.GroupScoreThousandths)} | {ReportMarkdown.FormatPoints(row.BaseScoreThousandths)} | {row.RoundWins} | {adv} |");
        }

        sb.AppendLine();
        if (group.Cutoff is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Cutoff (official): P16 {ReportMarkdown.Cell(group.Cutoff.P16.Name)} ({ReportMarkdown.FormatPoints(group.Cutoff.P16.GroupScoreThousandths)}) vs P17 {ReportMarkdown.Cell(group.Cutoff.P17.Name)} ({ReportMarkdown.FormatPoints(group.Cutoff.P17.GroupScoreThousandths)}) · gap {ReportMarkdown.FormatPoints(group.Cutoff.GapThousandths)}.");
            sb.AppendLine();
        }
    }

    private static void AppendFinal(StringBuilder sb, ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(data);

        sb.AppendLine(CultureInfo.InvariantCulture, $"## Official Final — P1..P32");
        sb.AppendLine();
        if (data.FinalStandings.Count == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Final: pending — no official Final standings yet. Finished groups are listed above; qualifier success is not a title.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"| Place | Card | Final score | Round W | Medal |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- | --- | --- | --- |");
        foreach (ChaosDossierData.Standing row in data.FinalStandings.OrderBy(r => r.GroupRank))
        {
            string medal = row.GroupRank == 1 ? "Gold" : row.GroupRank == 2 ? "Silver" : row.GroupRank == 3 ? "Bronze" : "—";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.GroupRank} | {ReportMarkdown.Cell(row.Name)} (id {row.AthleteId}) | {ReportMarkdown.FormatPoints(row.GroupScoreThousandths)} | {row.RoundWins} | {medal} |");
        }

        sb.AppendLine();
    }

    private static void AppendPaths(StringBuilder sb, ChaosDossierData data)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(data);

        sb.AppendLine(CultureInfo.InvariantCulture, $"## Paths ({data.Paths.Count} athletes, each exactly once)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Ordered by athlete id for determinism; exit-stage categories carry no global rank. `P#` is a group rank inside its completed group only.");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Card | Color | Source | Stages | Exit | Final |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- | --- | --- | --- | --- |");
        foreach (ChaosDossierData.Path path in data.Paths.OrderBy(p => p.AthleteId))
        {
            string journey = string.Join(" → ", path.Stages.OrderBy(s => s.StageIndex).Select(FormatPathStage));
            string exit = path.ExitStage ?? "active";
            string final = path.FinalPlace.HasValue ? $"P{path.FinalPlace.Value}" : "—";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {ReportMarkdown.Cell(path.Name)} (id {path.AthleteId}) | {ReportMarkdown.Cell(path.SportingColorName)} | {ReportMarkdown.Cell(path.SourceLabel)} | {ReportMarkdown.Cell(journey)} | {ReportMarkdown.Cell(exit)} | {final} |");
        }

        sb.AppendLine();
    }

    private static string FormatPathStage(ChaosDossierData.PathStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        string label = stage.IsFinal ? "F" : $"S{stage.StageIndex}G{stage.GroupNumber}";
        if (!stage.IsComplete)
        {
            return $"{label} ({stage.RoundsPlayed}/{stage.TotalRounds})";
        }

        string adv = stage.IsFinal ? string.Empty : stage.IsAdvanced.HasValue ? (stage.IsAdvanced.Value ? " ✓" : " ✗") : string.Empty;
        return $"{label} P{stage.GroupRank}{adv}";
    }
}
