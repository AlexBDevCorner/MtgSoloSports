using System.Globalization;
using System.Text;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.UniverseAlmanac;

internal static class UniverseAlmanacMarkdown
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        UniverseAlmanacData data)
    {
        StringBuilder sb = new();
        AppendHeader(sb, saveId, metadata, rules, data);
        AppendSeasons(sb, data);
        AppendSuperleague(sb, data);
        AppendFeeders(sb, data);
        AppendCups(sb, data);
        AppendRecords(sb, data);
        AppendLeaders(sb, data);
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, Guid saveId, SaveMetadataEntity metadata, SimulationKernel.Rules.RulesV1 rules, UniverseAlmanacData data)
    {
        sb.AppendLine("# Universe almanac — all-time reference");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Save: {metadata.Name} (`{saveId:D}`) · Seasons {data.FromSeasonNumber}–{data.ToSeasonNumber} ({data.SeasonCount} seasons) · Current Season {metadata.CurrentSeason} · Phase {metadata.Phase}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Scope: all-time curated reference as of this snapshot · Report: {ReportContract.UniverseAlmanacReportType} v{ReportContract.SchemaVersion} · Rules v{rules.Version} · Schema v{metadata.SchemaVersion}");
        sb.AppendLine();
        sb.AppendLine("- Freshness: uploads become stale as simulation advances; re-export this almanac occasionally and a fresh season dossier for new coverage before quoting.");
        sb.AppendLine("- Units: weighted Hall of Fame career points measure all-time greatness (weights version labelled below); official Total league points are fixed-point thousandths (divide by 1000) summed from final season tables and never mixed with weighted points.");
        sb.AppendLine("- Type Cup qualification never awards titles; only Final ranks 1–3 hold official medals. Color individual and team events are separate titles.");
        sb.AppendLine();
    }

    private static void AppendSeasons(StringBuilder sb, UniverseAlmanacData data)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Seasons ({data.Seasons.Count}) — champions and Cup states");
        sb.AppendLine();
        if (data.Seasons.Count == 0)
        {
            sb.AppendLine("No seasons yet.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Season | Complete | Superleague champion | Color Cup | Type Cup |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (AlmanacSeasonEntry season in data.Seasons)
        {
            string champion = season.SuperleagueChampionName ?? "— (no Superleague in Season 1)";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {season.SeasonNumber} | {(season.IsComplete ? "final" : "provisional")} | {ReportMarkdown.Cell(champion)} | {season.ColorCupState} | {season.TypeCupState} |");
        }

        sb.AppendLine();
    }

    private static void AppendSuperleague(StringBuilder sb, UniverseAlmanacData data)
    {
        sb.AppendLine("## Superleague podiums — official final tables only");
        sb.AppendLine();
        if (data.SuperleaguePodiums.Count == 0)
        {
            sb.AppendLine("No Superleague seasons yet (Season 1 predates the Superleague).");
            sb.AppendLine();
            return;
        }

        foreach (SuperleaguePodiumEntry podium in data.SuperleaguePodiums)
        {
            if (podium.Champion is null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- Season {podium.SeasonNumber} {(podium.IsComplete ? "final" : "provisional")}: {(podium.IsComplete ? "— (no persisted champion)" : "pending — table not final; omitted, never inferred")}");
                continue;
            }

            string runner = podium.RunnerUp is null ? "—" : $"{ReportMarkdown.Cell(podium.RunnerUp.AthleteName)} (id {podium.RunnerUp.AthleteId})";
            string third = podium.Third is null ? "—" : $"{ReportMarkdown.Cell(podium.Third.AthleteName)} (id {podium.Third.AthleteId})";
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Season {podium.SeasonNumber}: Champion {ReportMarkdown.Cell(podium.Champion.AthleteName)} (id {podium.Champion.AthleteId}) · {ReportMarkdown.FormatPoints(podium.Champion.PointsThousandths)} · Runner-up {runner} · Third {third}");
        }

        sb.AppendLine();
    }

    private static void AppendFeeders(StringBuilder sb, UniverseAlmanacData data)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Feeder champions ({data.FeederTitles.Count} titles) — other leagues as appropriate");
        sb.AppendLine();
        if (data.FeederTitles.Count == 0)
        {
            sb.AppendLine("No feeder titles yet.");
            sb.AppendLine();
            return;
        }

        foreach (FeederTitleEntry title in data.FeederTitles.OrderBy(t => t.SeasonNumber).ThenBy(t => t.LeagueName, StringComparer.Ordinal))
        {
            string champion = title.Champion is null
                ? (title.IsComplete ? "— (no persisted champion)" : "pending")
                : $"{ReportMarkdown.Cell(title.Champion.AthleteName)} (id {title.Champion.AthleteId})";
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Season {title.SeasonNumber} {ReportMarkdown.Cell(title.LeagueName)} ({title.Level}): {champion}");
        }

        sb.AppendLine();
    }

    private static void AppendCups(StringBuilder sb, UniverseAlmanacData data)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Cup editions ({data.CupEditions.Count}) — qualification vs Final separated");
        sb.AppendLine();
        if (data.CupEditions.Count == 0)
        {
            sb.AppendLine("No Cup editions selected yet.");
            sb.AppendLine();
        }
        else
        {
            foreach (AlmanacCupEditionEntry edition in data.CupEditions)
            {
                string podium = edition.Podium.Count == 0
                    ? "no official podium yet"
                    : string.Join(", ", edition.Podium.Select(p => $"P{p.TeamRank} {ReportMarkdown.Cell(p.TeamName)} ({ReportMarkdown.Cell(p.Medal)})"));
                string individual = edition.IndividualChampionName is null ? string.Empty : $" · Individual champion {ReportMarkdown.Cell(edition.IndividualChampionName)} (id {edition.IndividualChampionAthleteId?.ToString(CultureInfo.InvariantCulture) ?? "—"})";
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {edition.Cup} Cup Season {edition.SourceSeasonNumber} {edition.State}: {podium}{individual}");
            }

            sb.AppendLine();
        }

        AppendMedalTable(sb, "Color Cup team medals — all-time by color", data.ColorTeamTable);
        AppendColorRows(sb, "Color Cup individual medals — all-time by color (completed editions only)", data.ColorIndividualMedals);
        AppendColorRows(sb, "Color Cup combined medals — team + individual (completed editions only)", data.ColorCombinedMedals);
        AppendMedalTable(sb, "Type Cup team medals — all-time by creature type (Final only; qualification never awards medals)", data.TypeTeamTable);
    }

    private static void AppendMedalTable(StringBuilder sb, string title, IReadOnlyList<Cups.ListCupEditions.ListCupEditionsResponse.TeamSummary> table)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"### {title}");
        sb.AppendLine();
        if (table.Count == 0)
        {
            sb.AppendLine("No finished Cups yet.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Team | Editions | Gold | Silver | Bronze | Best | Last |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (Cups.ListCupEditions.ListCupEditionsResponse.TeamSummary row in table)
        {
            string best = row.BestRank.HasValue ? $"P{row.BestRank.Value}" : "—";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {ReportMarkdown.Cell(row.TeamName)} | {row.Editions} | {row.Gold} | {row.Silver} | {row.Bronze} | {best} | S{row.LastSeasonNumber} |");
        }

        sb.AppendLine();
    }

    private static void AppendColorRows(StringBuilder sb, string title, IReadOnlyList<Cups.ListCupEditions.ListCupEditionsResponse.ColorMedalRow> table)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"### {title}");
        sb.AppendLine();
        if (table.Count == 0)
        {
            sb.AppendLine("No completed Color Cups yet.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Color | Gold | Silver | Bronze |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (Cups.ListCupEditions.ListCupEditionsResponse.ColorMedalRow row in table)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {ReportMarkdown.Cell(row.TeamName)} | {row.Gold} | {row.Silver} | {row.Bronze} |");
        }

        sb.AppendLine();
    }

    private static void AppendRecords(StringBuilder sb, UniverseAlmanacData data)
    {
        sb.AppendLine("## Official career records — scope: all-time, all leagues");
        sb.AppendLine();
        foreach (AlmanacCareerRecordEntry record in data.CareerRecords)
        {
            string holders = record.Holders.Count == 0
                ? "vacant"
                : string.Join(", ", record.Holders.Select(h => $"{ReportMarkdown.Cell(h.AthleteName)} (id {h.AthleteId})"));
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {ReportMarkdown.Cell(record.Label)} (`{record.RecordKey}`): {ReportMarkdown.Cell(record.ValueDisplay)} · Holders: {holders}");
        }

        sb.AppendLine();
    }

    private static void AppendLeaders(StringBuilder sb, UniverseAlmanacData data)
    {
        AppendWeightedLeaders(sb, data);
        AppendPointsLeaders(sb, data);
        AppendStreaks(sb, data);
    }

    private static void AppendWeightedLeaders(StringBuilder sb, UniverseAlmanacData data)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## All-time Career Leaders — weighted career points v{data.WeightedLeaders.WeightsVersion} (not league points)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Covers {data.WeightedLeaders.TotalAthletes} athletes, active and inactive; ordered by weighted career points, major wins, Superleague titles, Superleague points, name, id.");
        sb.AppendLine();
        sb.AppendLine("| Rank | Card | Career pts | Maj wins | SL titles | SL pts |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (Records.GetHallOfFame.HallOfFameEntry leader in data.WeightedLeaders.Leaders)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {leader.Rank} | {ReportMarkdown.Cell(leader.AthleteName)} (id {leader.AthleteId}) | {leader.CareerPoints} | {leader.MajorWins} | {leader.SuperleagueTitles} | {leader.SuperleaguePoints} |");
        }

        sb.AppendLine();
    }

    private static void AppendPointsLeaders(StringBuilder sb, UniverseAlmanacData data)
    {
        sb.AppendLine("## Superleague points leaders — official Total league points (different statistic)");
        sb.AppendLine();
        sb.AppendLine("Sum of final Superleague championship points earned while competing in the Superleague (fixed-point thousandths, divide by 1000). Weighted career points above measure greatness; this table measures Superleague scoring.");
        sb.AppendLine();
        if (data.SuperleaguePointsLeaders.Count == 0)
        {
            sb.AppendLine("No Superleague seasons completed yet.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Rank | Card | Total league pts | Seasons | Titles | Best |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        int rank = 1;
        foreach (LeaguePointsLeaderEntry leader in data.SuperleaguePointsLeaders)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {rank} | {ReportMarkdown.Cell(leader.AthleteName)} (id {leader.AthleteId}) | {ReportMarkdown.FormatPoints(checked((int)Math.Clamp(leader.TotalLeaguePointsThousandths, int.MinValue, int.MaxValue)))} | {leader.CompletedSeasons} | {leader.Titles} | P{leader.BestRank} |");
            rank++;
        }

        sb.AppendLine();
    }

    private static void AppendStreaks(StringBuilder sb, UniverseAlmanacData data)
    {
        sb.AppendLine("## Consecutive-title streaks — historically grounded, computed exactly");
        sb.AppendLine();
        if (data.TitleStreaks.Count == 0)
        {
            sb.AppendLine("- No multi-season title streaks yet.");
        }
        else
        {
            foreach (StreakEntry streak in data.TitleStreaks)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {ReportMarkdown.Cell(streak.AthleteName)} (id {streak.AthleteId}): {streak.Streak} consecutive champion seasons");
            }
        }

        sb.AppendLine();
        if (data.StageWinStreaks.Count == 0)
        {
            sb.AppendLine("- No multi-stage win streaks yet.");
        }
        else
        {
            foreach (StreakEntry streak in data.StageWinStreaks)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {ReportMarkdown.Cell(streak.AthleteName)} (id {streak.AthleteId}): {streak.Streak} consecutive stage wins");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Inactive athletes are included everywhere above; career totals never exclude a card for leaving the active roster. Feeder-specific points leaders live in each league profile report.");
        sb.AppendLine();
    }
}
