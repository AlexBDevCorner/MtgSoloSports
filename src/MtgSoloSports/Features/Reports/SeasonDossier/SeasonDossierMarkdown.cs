using System.Globalization;
using System.Text;
using MtgSoloSports.Features.Reports.StandingsReport;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.SeasonDossier;

internal static class SeasonDossierMarkdown
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        SeasonDossierData data)
    {
        StringBuilder sb = new();
        AppendHeader(sb, saveId, metadata, rules, seasonCount, data);
        AppendSuperleague(sb, data);
        AppendProgression(sb, data);
        AppendSuperleaguePlacements(sb, data);
        AppendFeeders(sb, data);
        AppendPostseason(sb, data);
        AppendCups(sb, data);
        AppendHistory(sb, data);
        AppendRecords(sb, data);
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, Guid saveId, SaveMetadataEntity metadata, SimulationKernel.Rules.RulesV1 rules, int seasonCount, SeasonDossierData data)
    {
        string statusLabel = data.IsFinal ? "Complete · final table" : "In progress · provisional table";
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Season dossier — Season {data.SeasonNumber}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Save: {metadata.Name} (`{saveId:D}`) · Season {data.SeasonNumber} (season id {data.SeasonId}) · {seasonCount} season(s) in save");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Scope: season {data.SeasonNumber} only · Snapshot: {statusLabel}, {data.CompletedStages}/{data.TotalStages} stages, global stage {data.GlobalStage}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Report: {ReportContract.SeasonDossierReportType} v{ReportContract.SchemaVersion} · Rules v{rules.Version} · Schema v{metadata.SchemaVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)} (UTC; metadata only)");
        sb.AppendLine();
        string checksum = string.IsNullOrEmpty(data.SeasonChecksum) ? "pending (no completed stages)" : $"`{data.SeasonChecksum}`";
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Status: {data.Status} (isFinal={data.IsFinal.ToString().ToLowerInvariant()}) · Checksum {checksum}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Current save: Season {metadata.CurrentSeason} · Phase {metadata.Phase}");
        sb.AppendLine("- Freshness: uploads become stale as simulation advances; re-export after new stages or postseason events before quoting.");
        sb.AppendLine();
        sb.AppendLine("- Units: championship points are official season totals in fixed-point thousandths (divide by 1000, e.g. 77000 = 77.000; 32-position table, no bonus multiplier). Weighted Hall of Fame career points never appear in this dossier; leader titles are official league honours.");
        sb.AppendLine("- Type Cup qualification never awards titles; only Final ranks 1–3 hold official medals. Color individual and team events are separate titles. Provisional tables carry no zones; pending/not-held sections are omitted, never inferred.");
        sb.AppendLine();
    }

    private static void AppendSuperleague(StringBuilder sb, SeasonDossierData data)
    {
        string official = data.Superleague.IsOfficialSuperleague ? data.Superleague.LeagueName : $"{data.Superleague.LeagueName} (legacy fallback: this season predates the Superleague)";
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Superleague — {ReportMarkdown.Cell(official)}");
        sb.AppendLine();
        if (data.Superleague.Table.Count == 0)
        {
            sb.AppendLine("No standings yet. Complete a stage to populate championship standings.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Full table ({data.Superleague.Table.Count} rows, rank order) · {data.Status}");
        sb.AppendLine();
        sb.AppendLine("| Rank | Card | Championship pts | Stage W | Round W | Zone |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (GetStandingsReportHandler.StandingsReportRow row in data.Superleague.Table.OrderBy(r => r.Rank))
        {
            string champion = row.IsChampion ? " · Champion" : string.Empty;
            string zone = row.ZoneLabel ?? "— (provisional)";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.Rank}{champion} | {ReportMarkdown.Cell(row.Name)} (id {row.AthleteId}) | {ReportMarkdown.FormatPoints(row.TotalChampionshipPointsThousandths)} | {row.StageWins} | {row.RoundWins} | {ReportMarkdown.Cell(zone)} |");
        }

        sb.AppendLine();
    }

    private static void AppendProgression(StringBuilder sb, SeasonDossierData data)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Stage progression ({data.StageProgression.Count} completed stages)");
        sb.AppendLine();
        if (data.StageProgression.Count == 0)
        {
            sb.AppendLine("No completed stages yet; stage winners appear after Stage 1 persists.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Stage | Winner | Championship pts (stage) |");
        sb.AppendLine("| --- | --- | --- |");
        foreach (StageWinnerEntry stage in data.StageProgression.OrderBy(s => s.StageNumber))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {stage.StageNumber} | {ReportMarkdown.Cell(stage.WinnerName)} (id {stage.WinnerAthleteId?.ToString(CultureInfo.InvariantCulture) ?? "—"}) | {ReportMarkdown.FormatPoints(stage.WinnerPointsThousandths)} |");
        }

        sb.AppendLine();
    }

    private static void AppendSuperleaguePlacements(StringBuilder sb, SeasonDossierData data)
    {
        IReadOnlyList<int> stages = StagePlacementReport.StageNumbers(data.Superleague.StagePlacements);
        if (data.Superleague.IsOfficialSuperleague)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"## Superleague stage placements ({stages.Count} completed stages)");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"## Superleague stage placements — legacy fallback: {ReportMarkdown.Cell(data.Superleague.LeagueName)} (this season predates the Superleague) ({stages.Count} completed stages)");
        }

        sb.AppendLine();
        if (stages.Count == 0 || data.Superleague.StagePlacements.Count == 0)
        {
            sb.AppendLine("No completed stages yet. Stage placements appear after Stage 1 persists; an unplayed or in-progress stage is never assigned a rank.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("Superleague stage-by-stage journey for every league athlete: actual per-stage finish (P1-P32) in chronological stage order; not cumulative season rank. Rows follow the full table rank order. Winners-only stage progression above is preserved separately. Exact per-stage points, earned bonus, stage score and round wins are in the JSON dossier's superleague stagePlacements section (fixed-point thousandths). Stage championship points are per stage; superleague totals are cumulative. Earned bonus applies only from the next stage.");
        sb.AppendLine();
        IReadOnlyList<GetStandingsReportHandler.StandingsReportRow> ordered =
            data.Superleague.Table.OrderBy(r => r.Rank).ToList();
        StagePlacementReport.AppendMatrixBlocks(sb, ordered, data.Superleague.StagePlacements, stages);
        sb.AppendLine("Incomplete or future stages have no cell; absent never means P32, zero points or a completed stage.");
        sb.AppendLine();
    }

    private static void AppendFeeders(StringBuilder sb, SeasonDossierData data)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Feeder leagues ({data.FeederSummaries.Count}) — concise summaries");
        sb.AppendLine();
        if (data.FeederSummaries.Count == 0)
        {
            sb.AppendLine("No feeder leagues in this season.");
            sb.AppendLine();
            return;
        }

        foreach (FeederSummaryEntry feeder in data.FeederSummaries)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {ReportMarkdown.Cell(feeder.LeagueName)} · {feeder.Level} · {feeder.Status} · {feeder.CompletedStages} stages");
            sb.AppendLine();
            if (feeder.Champion is null)
            {
                sb.AppendLine("Champion pending; final table not yet persisted.");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"- Champion: {ReportMarkdown.Cell(feeder.Champion.AthleteName)} (id {feeder.Champion.AthleteId}) · {ReportMarkdown.FormatPoints(feeder.Champion.PointsThousandths)}");
            if (feeder.RunnerUp is not null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- Runner-up: {ReportMarkdown.Cell(feeder.RunnerUp.AthleteName)} (id {feeder.RunnerUp.AthleteId}) · {ReportMarkdown.FormatPoints(feeder.RunnerUp.PointsThousandths)}");
            }

            if (feeder.Third is not null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- Third: {ReportMarkdown.Cell(feeder.Third.AthleteName)} (id {feeder.Third.AthleteId}) · {ReportMarkdown.FormatPoints(feeder.Third.PointsThousandths)}");
            }

            sb.AppendLine();
        }
    }

    private static void AppendPostseason(StringBuilder sb, SeasonDossierData data)
    {
        sb.AppendLine("## Postseason — movement / qualifier / rebalance");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Movement: {ReportMarkdown.Cell(data.Postseason.MovementStatus)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Qualifier: {ReportMarkdown.Cell(data.Postseason.QualifierStatus)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Rebalance: {ReportMarkdown.Cell(data.Postseason.RebalanceStatus)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- {ReportMarkdown.Cell(data.Postseason.Summary)}");
        sb.AppendLine();
        AppendPostseasonMovements(sb, data.Postseason);
        AppendPostseasonQualifiers(sb, data.Postseason);
    }

    private static void AppendPostseasonMovements(StringBuilder sb, PostseasonSection postseason)
    {
        if (postseason.Movements.Count == 0)
        {
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"### Movement details ({postseason.Movements.Count} rows, full table)");
        sb.AppendLine();
        sb.AppendLine("| Athlete | Kind | From → To | Source rank |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (PostseasonMovementEntry move in postseason.Movements.OrderBy(m => m.Kind, StringComparer.Ordinal).ThenBy(m => m.AthleteId))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {ReportMarkdown.Cell(move.AthleteName)} (id {move.AthleteId}) | {ReportMarkdown.Cell(move.Kind)} | {ReportMarkdown.Cell(move.FromLeagueName)} → {ReportMarkdown.Cell(move.ToLeagueName)} | {move.FromSeasonRank} |");
        }

        sb.AppendLine();
    }

    private static void AppendPostseasonQualifiers(StringBuilder sb, PostseasonSection postseason)
    {
        if (postseason.Qualifiers.Count == 0)
        {
            if (string.Equals(postseason.QualifierStatus, "not-applicable", StringComparison.Ordinal))
            {
                sb.AppendLine("Qualifier: not-applicable — no qualifier is scheduled for this transition (e.g. Season 1 inaugural).");
                sb.AppendLine();
            }

            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"### Qualifier standings ({postseason.Qualifiers.Count} rows, full table)");
        sb.AppendLine();
        sb.AppendLine("| Rank | Card | Boundary | Role | Score | Qualified |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (PostseasonQualifierEntry qualifier in postseason.Qualifiers.OrderBy(q => q.QualifierBoundary).ThenBy(q => q.QualifierSportingColor).ThenBy(q => q.QualifierRank))
        {
            string boundary = $"{ReportMarkdown.Cell(qualifier.QualifierBoundaryLabel)} ({qualifier.QualifierBoundary})";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {qualifier.QualifierRank} | {ReportMarkdown.Cell(qualifier.AthleteName)} (id {qualifier.AthleteId}) | {boundary} | {ReportMarkdown.Cell(qualifier.Role)} | {ReportMarkdown.FormatPoints(qualifier.QualifierScoreThousandths)} | {(qualifier.IsQualified ? "yes" : "no")} |");
        }

        sb.AppendLine();
    }

    private static void AppendCups(StringBuilder sb, SeasonDossierData data)
    {
        AppendColorCup(sb, data.ColorCup);
        AppendTypeCup(sb, data.TypeCup);
    }

    private static void AppendColorCup(StringBuilder sb, CupSummaryEntry? color)
    {
        sb.AppendLine("## Color Cup");
        sb.AppendLine();
        if (color is null || !color.HasSelection)
        {
            sb.AppendLine("Color Cup not yet selected for this season; no squads or events persisted.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"- Edition state: {color.State} · {ReportMarkdown.Cell(color.Note)}");
        sb.AppendLine();
        if (color.Individual is null)
        {
            sb.AppendLine("Individual event: pending — no persisted standings yet.");
        }
        else if (color.Individual.ChampionName is null)
        {
            sb.AppendLine("Individual event: pending — squads final, results omitted until played.");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Individual champion: {ReportMarkdown.Cell(color.Individual.ChampionName)} (id {color.Individual.ChampionAthleteId?.ToString(CultureInfo.InvariantCulture) ?? "—"})");
            foreach (CupPodiumEntry podium in color.Individual.TopThree)
            {
                string athlete = podium.AthleteId.HasValue
                    ? $"{ReportMarkdown.Cell(podium.AthleteName ?? $"Athlete {podium.AthleteId.Value}")} (id {podium.AthleteId.Value}) · {ReportMarkdown.Cell(podium.TeamName)}"
                    : ReportMarkdown.Cell(podium.TeamName);
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - P{podium.Rank} {athlete} · {ReportMarkdown.FormatPoints(podium.ScoreThousandths)} · {ReportMarkdown.Cell(podium.Medal)}");
            }
        }

        sb.AppendLine();
        if (color.Team is null || color.Team.ChampionTeamName is null)
        {
            sb.AppendLine("Team event: pending — no persisted team standings yet.");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Team champion: {ReportMarkdown.Cell(color.Team.ChampionTeamName)} (`{ReportMarkdown.Cell(color.Team.ChampionTeamKey ?? string.Empty)}`)");
            foreach (CupPodiumEntry podium in color.Team.TopThree)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - P{podium.Rank} {ReportMarkdown.Cell(podium.TeamName)} · {ReportMarkdown.FormatPoints(podium.ScoreThousandths)} · {ReportMarkdown.Cell(podium.Medal)}");
            }

            if (color.Team.WinningRoster.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - Winning roster: {string.Join(", ", color.Team.WinningRoster.Select(r => $"{ReportMarkdown.Cell(r.AthleteName)} (id {r.AthleteId})"))}");
            }
        }

        sb.AppendLine();
    }

    private static void AppendTypeCup(StringBuilder sb, CupSummaryEntry? type)
    {
        sb.AppendLine("## Type Cup — qualification is not a title");
        sb.AppendLine();
        if (type is null || !type.HasSelection)
        {
            sb.AppendLine("Type Cup not yet selected for this season; no squads or events persisted.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"- Edition state: {type.State} · {ReportMarkdown.Cell(type.Note)}");
        sb.AppendLine();
        if (type.Tournament is null)
        {
            sb.AppendLine("Tournament: pending — no qualification or Final rows yet.");
            sb.AppendLine();
            return;
        }

        TypeCupSummary tournament = type.Tournament;
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Qualification: {tournament.FinishedQualificationGroups}/{tournament.QualificationGroupCount} groups finished{(tournament.IsDirectFinal ? " (direct Final, no qualification stage)" : string.Empty)}");
        if (tournament.QualifiedTeams.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Qualified for Final: {string.Join(", ", tournament.QualifiedTeams.Select(ReportMarkdown.Cell))}");
        }

        AppendTypeQualificationGroups(sb, tournament);

        if (tournament.ChampionTeamKey is null)
        {
            sb.AppendLine("Final: pending — no official Final standings yet; qualifier success is not a title.");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Final champion: {ReportMarkdown.Cell(tournament.ChampionTeamKey)}");
            foreach (CupPodiumEntry podium in tournament.FinalTopThree)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - P{podium.Rank} {ReportMarkdown.Cell(podium.TeamName)} · {ReportMarkdown.FormatPoints(podium.ScoreThousandths)} · {ReportMarkdown.Cell(podium.Medal)}");
            }

            if (tournament.WinningRoster.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - Winning roster: {string.Join(", ", tournament.WinningRoster.Select(r => $"{ReportMarkdown.Cell(r.AthleteName)} (id {r.AthleteId})"))}");
            }
        }

        sb.AppendLine();
    }

    private static void AppendTypeQualificationGroups(StringBuilder sb, TypeCupSummary tournament)
    {
        if (tournament.QualificationGroups.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Qualification groups ({tournament.QualificationGroups.Count} finished of {tournament.QualificationGroupCount}) — not titles; cutoff decides Final entry");
        sb.AppendLine();
        bool finalPending = tournament.ChampionTeamKey is null;
        foreach (TypeCupQualificationGroupEntry group in tournament.QualificationGroups.OrderBy(g => g.QualificationGroup))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"#### Group {group.QualificationGroup} — {group.GroupSize} teams · {group.GuaranteedPlaces} guaranteed · Checksum `{group.Checksum}`");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(group.WildcardCandidate))
            {
                string fate = !string.IsNullOrWhiteSpace(group.WildcardWinner)
                    ? $"wildcard → Final ({ReportMarkdown.Cell(group.WildcardWinner)})"
                    : finalPending
                        ? "pending — competes for a wildcard once all groups complete"
                        : "eliminated";
                sb.AppendLine(CultureInfo.InvariantCulture, $"Wildcard candidate: {ReportMarkdown.Cell(group.WildcardCandidate)} — {fate}.");
                sb.AppendLine();
            }

            sb.AppendLine("| Rank | Team | Score | Status |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (TypeCupQualificationTeamEntry row in group.Teams.OrderBy(t => t.TeamRank))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.TeamRank} | {ReportMarkdown.Cell(row.CreatureType)} | {ReportMarkdown.FormatPoints(row.TeamScoreThousandths)} | {ReportMarkdown.Cell(row.QualificationStatus)} |");
            }

            sb.AppendLine();
        }

        if (tournament.Wildcards.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Wildcard places ({tournament.Wildcards.Count}, policy v{tournament.QualificationPolicyVersion}): {string.Join(", ", tournament.Wildcards.OrderBy(w => w.CreatureType, StringComparer.Ordinal).Select(w => ReportMarkdown.Cell(w.CreatureType)))}");
            sb.AppendLine();
        }
    }

    private static void AppendHistory(StringBuilder sb, SeasonDossierData data)
    {
        sb.AppendLine("## Historical perspective — facts only, labelled by season");
        sb.AppendLine();
        if (data.History.PreviousChampion is null)
        {
            sb.AppendLine("- Previous-season champion: none (Season 1 has no defender).");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Previous-season champion (Season {data.History.PreviousSeasonNumber}): {ReportMarkdown.Cell(data.History.PreviousChampion.AthleteName)} (id {data.History.PreviousChampion.AthleteId}) · {ReportMarkdown.FormatPoints(data.History.PreviousChampion.PointsThousandths)}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Defending champion in Season {data.SeasonNumber}: {ReportMarkdown.Cell(data.History.DefendingChampion?.AthleteName ?? "—")} (same athlete as previous champion where applicable).");
        }

        sb.AppendLine();
        sb.AppendLine("### Champions, last five seasons");
        sb.AppendLine();
        foreach (ChampionSeasonEntry entry in data.History.ChampionsLastFive)
        {
            string champion = entry.Champion is null
                ? (!entry.HasSuperleague
                    ? $"— (no Superleague in Season {entry.SeasonNumber})"
                    : entry.IsComplete ? "— (no persisted champion; corrupted if final)" : "pending (season in progress)")
                : $"{ReportMarkdown.Cell(entry.Champion.AthleteName)} (id {entry.Champion.AthleteId}) · {ReportMarkdown.FormatPoints(entry.Champion.PointsThousandths)}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Season {entry.SeasonNumber} {(entry.IsComplete ? "final" : "provisional")}: {champion}");
        }

        sb.AppendLine();
        sb.AppendLine("### Current leaders — official league titles (honours), not weighted career points");
        sb.AppendLine();
        if (data.History.LeaderCareers.Count == 0)
        {
            sb.AppendLine("No leaders yet.");
        }
        else
        {
            foreach (LeaderCareerEntry leader in data.History.LeaderCareers)
            {
                string rank = leader.CurrentRank.HasValue ? $"P{leader.CurrentRank.Value}" : "unranked";
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {rank} {ReportMarkdown.Cell(leader.AthleteName)} (id {leader.AthleteId}) · {ReportMarkdown.FormatPoints(leader.CurrentPointsThousandths)} · Superleague titles {leader.SuperleagueTitles} · Feeder titles {leader.FeederTitles} · {leader.SeasonsActive} active seasons");
            }
        }

        sb.AppendLine();
        sb.AppendLine("### Major Cup performers — career titles");
        sb.AppendLine();
        if (data.History.CupPerformerCareers.Count == 0)
        {
            sb.AppendLine("No Cup champions yet, or champions already listed among leaders.");
        }
        else
        {
            foreach (LeaderCareerEntry performer in data.History.CupPerformerCareers)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {ReportMarkdown.Cell(performer.AthleteName)} (id {performer.AthleteId}) · Superleague titles {performer.SuperleagueTitles} · Feeder titles {performer.FeederTitles} · {performer.SeasonsActive} active seasons");
            }
        }

        sb.AppendLine();
    }

    private static void AppendRecords(StringBuilder sb, SeasonDossierData data)
    {
        sb.AppendLine("## Official career records — scope: all-time, all leagues");
        sb.AppendLine();
        if (data.CareerRecords.Count == 0)
        {
            sb.AppendLine("No records computed.");
            sb.AppendLine();
            return;
        }

        foreach (CareerRecordEntry record in data.CareerRecords)
        {
            string holders = record.Holders.Count == 0
                ? "vacant"
                : string.Join(", ", record.Holders.Select(h => $"{ReportMarkdown.Cell(h.AthleteName)} (id {h.AthleteId})"));
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {ReportMarkdown.Cell(record.Label)} (`{record.RecordKey}`): {ReportMarkdown.Cell(record.ValueDisplay)} · Holders: {holders}");
        }

        sb.AppendLine();
        sb.AppendLine("Streaks above are consecutive-season (titles) or consecutive-stage (stage wins) runs computed exactly from persisted standings; ties list every joint holder. Scoring maxima (single-round/stage/event) live on the Records page and are omitted here by design to keep the dossier compact.");
        sb.AppendLine();
    }
}
