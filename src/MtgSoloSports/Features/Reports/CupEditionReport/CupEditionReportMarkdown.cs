using System.Globalization;
using System.Text;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.GetColorCupTeamResult;
using MtgSoloSports.Features.Cups.GetTypeCupTournament;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Reports.CupEditionReport;

/// <summary>
/// Markdown rendering for Cup edition reports. Display-only; never resimulates.
/// </summary>
internal static class CupEditionReportMarkdown
{
    internal static string Build(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetCupEditionReportHandler.ReportData data)
    {
        StringBuilder sb = new();
        AppendHeader(sb, saveId, metadata, rules, seasonCount, data);
        AppendSquads(sb, data);
        if (string.Equals(data.Cup, "Color", StringComparison.Ordinal))
        {
            AppendColorIndividual(sb, data.ColorIndividual);
            AppendColorTeam(sb, data.ColorTeam);
        }
        else
        {
            AppendTypeTournament(sb, data.TypeTournament);
        }

        return sb.ToString();
    }

    private static void AppendHeader(
        StringBuilder sb,
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        GetCupEditionReportHandler.ReportData data)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {data.Cup} Cup edition report — Season {data.SourceSeasonNumber}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Save: {metadata.Name} (`{saveId:D}`) · Season {metadata.CurrentSeason} · Phase {metadata.Phase}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Scope: {data.Cup} Cup, source season {data.SourceSeasonNumber} (never substituted with another edition)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Report: {ReportContract.CupEditionReportType} v{ReportContract.SchemaVersion} · Rules v{rules.Version} · Schema v{metadata.SchemaVersion}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Edition state: {data.State} · Teams: {data.TeamCount} · Full ranking stored: {(data.HasFullRanking ? "yes" : "no (selected members only)")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Snapshot: {seasonCount} season(s) in save; this report reads exactly source season {data.SourceSeasonNumber}.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Units: team/individual scores are fixed-point thousandths (divide by 1000). `—` means unavailable; 0 is an exact zero.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Type Cup qualification tables never award titles; only Final ranks 1–3 hold official medals.");
        sb.AppendLine();
    }

    private static void AppendSquads(StringBuilder sb, GetCupEditionReportHandler.ReportData data)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Squads ({data.Squads.Count} teams)");
        sb.AppendLine();
        if (data.Squads.Count == 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"No squads persisted for this edition.");
            sb.AppendLine();
            return;
        }

        foreach (GetCupEditionReportHandler.SquadTeam team in data.Squads)
        {
            AppendSquadTeam(sb, team);
        }
    }

    private static void AppendSquadTeam(StringBuilder sb, GetCupEditionReportHandler.SquadTeam team)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"### {ReportMarkdown.Cell(team.TeamName)} (`{ReportMarkdown.Cell(team.TeamKey)}`)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| # | Card | Rating |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- | --- |");
        foreach (GetCupEditionReportHandler.SquadMember member in team.Members.OrderBy(m => m.SelectionRank))
        {
            string rank = member.TypeRank.HasValue
                ? $"#{member.SelectionRank} (type #{member.TypeRank.Value})"
                : $"#{member.SelectionRank}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {ReportMarkdown.Cell(rank)} | {ReportMarkdown.Cell(member.Name)} (id {member.AthleteId}) | {ReportMarkdown.FormatPoints(member.FinalRatingThousandths)} |");
        }

        sb.AppendLine();
    }

    private static void AppendColorIndividual(StringBuilder sb, GetColorCupIndividualResultResponse? individual)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Individual event");
        sb.AppendLine();
        if (individual is null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Individual event: pending — no persisted standings yet. Squads above are final.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Finished: {individual.Standings.Count} athletes · {individual.Rounds} rounds · Champion {ReportMarkdown.Cell(individual.ChampionName)} (id {individual.ChampionAthleteId}) · Checksum `{individual.Checksum}`.");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Rank | Card | Team | Cup score | Round W | Medal |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- | --- | --- | --- | --- |");
        foreach (GetColorCupIndividualMember row in individual.Standings.OrderBy(r => r.CupRank))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.CupRank} | {ReportMarkdown.Cell(row.Name)} (id {row.AthleteId}) | {ReportMarkdown.Cell(row.SportingColor)} | {ReportMarkdown.FormatPoints(row.CupScoreThousandths)} | {row.RoundWins} | {ReportMarkdown.Cell(row.Medal)} |");
        }

        sb.AppendLine();
    }

    private static void AppendColorTeam(StringBuilder sb, GetColorCupTeamResultResponse? team)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Team event");
        sb.AppendLine();
        if (team is null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Team event: pending — no persisted team standings yet.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Finished: {team.Teams.Count} teams · {team.GroupCount} rank groups × {team.GroupRounds} rounds · Champion {ReportMarkdown.Cell(team.ChampionTeamName)} · Checksum `{team.Checksum}`.");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Rank | Team | Score | Base | Group W | Round W | Medal |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- | --- | --- | --- | --- | --- |");
        foreach (GetColorCupTeamMember row in team.Teams.OrderBy(r => r.TeamRank))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.TeamRank} | {ReportMarkdown.Cell(row.TeamName)} | {ReportMarkdown.FormatPoints(row.TeamScoreThousandths)} | {ReportMarkdown.FormatPoints(row.TeamBaseThousandths)} | {row.GroupWins} | {row.RoundWins} | {ReportMarkdown.Cell(row.Medal)} |");
        }

        sb.AppendLine();
        AppendColorLegs(sb, team);
    }

    private static void AppendColorLegs(StringBuilder sb, GetColorCupTeamResultResponse team)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Group legs ({team.Legs.Count} rows)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Group | Rank | Card | Team | Leg score | Round W |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- | --- | --- | --- | --- |");
        foreach (GetColorCupTeamLegMember leg in team.Legs.OrderBy(l => l.GroupNumber).ThenBy(l => l.GroupRank))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {leg.GroupNumber} | {leg.GroupRank} | {ReportMarkdown.Cell(leg.Name)} (id {leg.AthleteId}) | {ReportMarkdown.Cell(leg.SportingColor)} | {ReportMarkdown.FormatPoints(leg.GroupScoreThousandths)} | {leg.RoundWins} |");
        }

        sb.AppendLine();
    }

    private static void AppendTypeTournament(StringBuilder sb, GetTypeCupTournamentResponse? tournament)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Tournament");
        sb.AppendLine();
        if (tournament is null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Final: pending — qualification may be in progress; no official Final standings yet. Qualifier success is not a title.");
            sb.AppendLine();
            return;
        }

        AppendTournamentFormat(sb, tournament);
        AppendQualGroups(sb, tournament);
        AppendWildcards(sb, tournament);
        AppendTournamentFinal(sb, tournament);
    }

    private static void AppendTournamentFormat(StringBuilder sb, GetTypeCupTournamentResponse tournament)
    {
        if (tournament.IsDirectFinal)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Format: direct Final · {tournament.TeamCount} teams · Champion {ReportMarkdown.Cell(tournament.ChampionCreatureType ?? "—")} · Checksum `{tournament.TournamentChecksum}`.");
            sb.AppendLine();
            return;
        }

        string checksum = string.IsNullOrWhiteSpace(tournament.TournamentChecksum) ? "pending (Final not played)" : $"`{tournament.TournamentChecksum}`";
        sb.AppendLine(CultureInfo.InvariantCulture, $"Format: qualification + fresh 32-team Final · {tournament.TeamCount} teams · {tournament.QualificationGroupCount} groups · Policy v{tournament.QualificationPolicyVersion} · Wildcards {tournament.WildcardCount} · Checksum {checksum}.");
        sb.AppendLine();
        if (tournament.Final is null && tournament.QualificationGroups.Count > 0 && tournament.QualificationGroups.Count < tournament.QualificationGroupCount)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Finished qualification groups: {tournament.QualificationGroups.Count}/{tournament.QualificationGroupCount}; remaining groups and the Final are pending and omitted.");
            sb.AppendLine();
        }
    }

    private static void AppendQualGroups(StringBuilder sb, GetTypeCupTournamentResponse tournament)
    {
        if (tournament.QualificationGroups.Count == 0)
        {
            return;
        }

        bool partial = tournament.Final is null && tournament.QualificationGroups.Count < tournament.QualificationGroupCount;
        string count = partial
            ? $"{tournament.QualificationGroups.Count} finished of {tournament.QualificationGroupCount}"
            : $"{tournament.QualificationGroups.Count}";
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Qualification groups ({count}) — not titles");
        sb.AppendLine();
        bool finalPending = tournament.Final is null;
        foreach (TypeCupTournamentQualificationGroup group in tournament.QualificationGroups.OrderBy(g => g.QualificationGroup))
        {
            AppendQualGroup(sb, group, finalPending);
        }
    }

    private static void AppendQualGroup(StringBuilder sb, TypeCupTournamentQualificationGroup group, bool finalPending)
    {
        string letter = CupTeamHistoryBuilder.GroupLetter(group.QualificationGroup);
        sb.AppendLine(CultureInfo.InvariantCulture, $"#### Group {letter} — {group.GroupSize} teams · {group.GuaranteedPlaces} guaranteed · Checksum `{group.Checksum}`");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(group.WildcardCandidate))
        {
            string fate = !string.IsNullOrWhiteSpace(group.WildcardWinner)
                ? $"wildcard → Final ({ReportMarkdown.Cell(group.WildcardWinner)})"
                : finalPending
                    ? "pending — Final not played yet; competes for a wildcard once all groups complete"
                    : "eliminated";
            sb.AppendLine(CultureInfo.InvariantCulture, $"Wildcard candidate: {ReportMarkdown.Cell(group.WildcardCandidate)} — {fate}.");
            sb.AppendLine();
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"| Rank | Team | Score | Status |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- | --- | --- |");
        foreach (TypeCupTournamentTeam row in group.Teams.OrderBy(t => t.TeamRank))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.TeamRank} | {ReportMarkdown.Cell(row.CreatureType)} | {ReportMarkdown.FormatPoints(row.TeamScoreThousandths)} | {ReportMarkdown.Cell(row.QualificationStatus)} |");
        }

        sb.AppendLine();
    }

    private static void AppendWildcards(StringBuilder sb, GetTypeCupTournamentResponse tournament)
    {
        if (tournament.Wildcards is not { Count: > 0 })
        {
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"### Wildcards ({tournament.Wildcards.Count}) — global performance places");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Team | Group | Rank | Adjusted numerator/denominator | Tie draw |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- | --- | --- | --- |");
        foreach (TypeCupTournamentWildcard wildcard in tournament.Wildcards.OrderBy(w => w.CreatureType, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {ReportMarkdown.Cell(wildcard.CreatureType)} | {CupTeamHistoryBuilder.GroupLetter(wildcard.QualificationGroup)} | P{wildcard.GroupRank} | {wildcard.NormalizedNumerator}/{wildcard.NormalizedDenominator} | {(wildcard.TieDraw ? "yes (seeded draw)" : "no")} |");
        }

        sb.AppendLine();
    }

    private static void AppendTournamentFinal(StringBuilder sb, GetTypeCupTournamentResponse tournament)
    {
        if (tournament.Final is null)
        {
            if (tournament.QualificationGroups.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Final: pending — no official Final standings yet. Finished qualification groups: {tournament.QualificationGroups.Count}/{tournament.QualificationGroupCount}; qualifier success is not a title.");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Final: pending — no official Final standings yet.");
            }

            sb.AppendLine();
            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"### Final — official standings ({tournament.Final.TeamCount} teams)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"| Rank | Team | Score | Medal |");
        sb.AppendLine(CultureInfo.InvariantCulture, $"| --- | --- | --- | --- |");
        foreach (TypeCupTournamentTeam row in tournament.Final.Teams.OrderBy(t => t.TeamRank))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {row.TeamRank} | {ReportMarkdown.Cell(row.CreatureType)} | {ReportMarkdown.FormatPoints(row.TeamScoreThousandths)} | {ReportMarkdown.Cell(row.Medal)} |");
        }

        sb.AppendLine();
    }
}
