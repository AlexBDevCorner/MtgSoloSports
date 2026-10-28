using System.Text;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.GetTypeCupSelectionReport;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Reports.CupTeamReport;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Builds a versioned,
/// standalone report for exactly one Cup team (color sporting color or Type
/// creature type) across every edition it was selected for. Reuses the
/// persisted squad/result/leg history plus stored selection reasons; never
/// substitutes current league affiliations and never resimulates. Read-only:
/// single AsNoTracking context in one committed read transaction.
/// </summary>
public sealed class GetCupTeamReportHandler
{
    private readonly SaveStore _store;

    public GetCupTeamReportHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public sealed record ReportFile(byte[] Content, string ContentType, string FileName);

    internal sealed record ReportData(string Cup, CupTeamHistoryResponse History);

    public async Task<ReportFile> HandleAsync(
        Guid saveId,
        string cup,
        string teamKey,
        string? format,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        string normalizedCup = ReportContract.NormalizeCup(cup);
        if (string.IsNullOrWhiteSpace(teamKey))
        {
            throw new ArgumentException("Team key must not be empty.", nameof(teamKey));
        }

        string normalized = ReportContract.NormalizeFormat(format);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        SimulationKernel.Rules.RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);

        ReportData data = normalizedCup switch
        {
            "color" => new ReportData("Color", await LoadColorHistoryAsync(context, saveId, teamKey, rules, cancellationToken).ConfigureAwait(false)),
            "type" => new ReportData("Type", await LoadTypeHistoryAsync(context, saveId, teamKey, rules, cancellationToken).ConfigureAwait(false)),
            _ => throw new ArgumentException($"Unsupported cup '{cup}'. Use 'color' or 'type'.", nameof(cup)),
        };
        int seasonCount = await context.Seasons.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(normalized, "md", StringComparison.Ordinal))
        {
            string markdown = CupTeamReportMarkdown.Build(saveId, metadata, rules, seasonCount, data);
            return new ReportFile(
                Encoding.UTF8.GetBytes(markdown),
                ReportContract.MarkdownContentType,
                ReportFilenames.CupTeamFileName(normalizedCup, data.History.TeamKey, "md"));
        }

        string json = CupTeamReportJson.Build(saveId, metadata, rules, seasonCount, data);
        return new ReportFile(
            Encoding.UTF8.GetBytes(json),
            ReportContract.JsonContentType,
            ReportFilenames.CupTeamFileName(normalizedCup, data.History.TeamKey, "json"));
    }

    internal static async Task<CupTeamHistoryResponse> LoadColorHistoryAsync(
        SaveDbContext context,
        Guid saveId,
        string teamKey,
        SimulationKernel.Rules.RulesV1 rules,
        CancellationToken cancellationToken)
    {
        if (!CupTeamKeys.TryParseColorKey(teamKey, out SportingColor color))
        {
            throw new CupTeamReportNotFoundException($"'{teamKey}' is not a Color Cup team.");
        }

        int colorValue = (int)color;
        List<ColorCupSelectionEntity> selections = await context.ColorCupSelections
            .AsNoTracking().Where(e => e.SportingColor == colorValue).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (selections.Count == 0)
        {
            throw new CupTeamReportNotFoundException($"Color Cup team '{teamKey}' has never been selected.");
        }

        List<ColorCupTeamStandingEntity> standings = await context.ColorCupTeamStandings
            .AsNoTracking().Where(e => e.SportingColor == colorValue).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings
            .AsNoTracking().Where(e => e.SportingColor == colorValue).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<ColorCupIndividualStandingEntity> individuals = await context.ColorCupIndividualStandings
            .AsNoTracking().Where(e => e.SportingColor == colorValue).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> seasons = selections.Select(s => s.SourceSeasonNumber).Distinct().ToList();
        Dictionary<int, CupTeamHistoryBuilder.SeasonFacts> facts = await LoadColorFactsAsync(context, seasons, cancellationToken).ConfigureAwait(false);
        List<int> athleteIds = selections.Select(s => s.SaveAthleteId).Distinct().ToList();
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking().Where(e => athleteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);

        return CupTeamHistoryBuilder.Build(new CupTeamHistoryBuilder.Input(
            saveId,
            CupTeamKeys.ColorCup,
            CupTeamKeys.ColorKey(color),
            CupTeamKeys.ColorName(color),
            rules.ColorCupTeamSize,
            selections.Select(s => new CupTeamHistoryBuilder.SelectionRow(
                s.SourceSeasonNumber, s.SaveAthleteId, s.SelectionRank,
                s.FinalRatingThousandths, s.BonusNormThousandths, s.PerformanceNormThousandths,
                s.FormNormThousandths, s.PrestigeNormThousandths, Reason: null)).ToList(),
            standings.Select(s => new CupTeamHistoryBuilder.StandingRow(
                s.SourceSeasonNumber, s.TeamRank, s.TeamScoreThousandths, s.TeamBaseThousandths,
                s.GroupWins, s.RoundWins, ((ColorCupMedal)s.Medal).ToString())).ToList(),
            legs.Select(l => new CupTeamHistoryBuilder.LegRow(
                l.SourceSeasonNumber, l.SaveAthleteId, l.GroupNumber, l.GroupRank,
                l.GroupScoreThousandths, l.BaseScoreThousandths, l.RoundWins)).ToList(),
            individuals.Select(i => new CupTeamHistoryBuilder.IndividualRow(
                i.SourceSeasonNumber, i.SaveAthleteId, i.CupRank, i.CupScoreThousandths, ((ColorCupMedal)i.Medal).ToString())).ToList(),
            facts,
            athletes));
    }

    internal static async Task<Dictionary<int, CupTeamHistoryBuilder.SeasonFacts>> LoadColorFactsAsync(
        SaveDbContext context,
        List<int> seasons,
        CancellationToken cancellationToken)
    {
        List<ColorCupSelectionEntity> fields = await context.ColorCupSelections
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber)).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> teamRoundSeasons = await context.ColorCupTeamRounds
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber))
            .Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> individualRoundSeasons = await context.ColorCupIndividualRounds
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber))
            .Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<int> individualDoneSeasons = await context.ColorCupIndividualStandings
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber))
            .Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        return seasons.ToDictionary(
            season => season,
            season => new CupTeamHistoryBuilder.SeasonFacts(
                fields.Where(f => f.SourceSeasonNumber == season).Select(f => f.SportingColor).Distinct().Count(),
                teamRoundSeasons.Contains(season) || individualRoundSeasons.Contains(season),
                individualDoneSeasons.Contains(season)));
    }

    internal sealed record TypeHistoryRows(
        List<TypeCupSelectionEntity> Selections,
        List<TypeCupSelectionEntity> Fields,
        List<TypeCupTeamStandingEntity> Standings,
        List<TypeCupTeamGroupStandingEntity> Legs,
        HashSet<int> SeasonsWithFinal,
        HashSet<int> TeamFinalSeasons,
        Dictionary<int, CupTeamHistoryBuilder.SeasonFacts> Facts,
        Dictionary<(int Season, int Athlete), string> Reasons,
        Dictionary<int, SaveAthleteEntity> Athletes);

    internal static async Task<CupTeamHistoryResponse> LoadTypeHistoryAsync(
        SaveDbContext context,
        Guid saveId,
        string teamKey,
        SimulationKernel.Rules.RulesV1 rules,
        CancellationToken cancellationToken)
    {
        TypeHistoryRows rows = await LoadTypeRowsAsync(context, teamKey, cancellationToken).ConfigureAwait(false);
        return MapTypeHistory(saveId, teamKey, rules, rows);
    }

    internal static async Task<TypeHistoryRows> LoadTypeRowsAsync(
        SaveDbContext context,
        string teamKey,
        CancellationToken cancellationToken)
    {
        List<TypeCupSelectionEntity> selections = await context.TypeCupSelections
            .AsNoTracking().Where(e => e.CreatureType == teamKey).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (selections.Count == 0)
        {
            throw new CupTeamReportNotFoundException($"Type Cup team '{teamKey}' has never been selected.");
        }

        List<int> seasons = selections.Select(s => s.SourceSeasonNumber).Distinct().ToList();
        List<TypeCupSelectionEntity> fields = await context.TypeCupSelections
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber)).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<TypeCupTeamStandingEntity> standings = await context.TypeCupTeamStandings
            .AsNoTracking().Where(e => e.CreatureType == teamKey).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .AsNoTracking().Where(e => e.CreatureType == teamKey).ToListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<int> seasonsWithFinal = await LoadSeasonsWithFinalAsync(context, seasons, cancellationToken).ConfigureAwait(false);
        HashSet<int> teamFinalSeasons = standings
            .Where(e => e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final)
            .Select(e => e.SourceSeasonNumber).ToHashSet();
        List<int> roundSeasons = await context.TypeCupTeamRounds
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber))
            .Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<int, CupTeamHistoryBuilder.SeasonFacts> facts = seasons.ToDictionary(
            season => season,
            season => new CupTeamHistoryBuilder.SeasonFacts(
                fields.Where(f => f.SourceSeasonNumber == season).Select(f => f.CreatureType).Distinct(StringComparer.Ordinal).Count(),
                roundSeasons.Contains(season),
                IndividualComplete: true));
        List<int> seasonIds = selections.Select(s => s.SourceSeasonId).Distinct().ToList();
        List<CupSelectionReportEntity> reports = await context.CupSelectionReports
            .AsNoTracking().Where(e => seasonIds.Contains(e.SourceSeasonId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<(int Season, int Athlete), string> reasons = ReadReasons(reports, teamKey);
        List<int> athleteIds = selections.Select(s => s.SaveAthleteId).Distinct().ToList();
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking().Where(e => athleteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
        return new TypeHistoryRows(selections, fields, standings, legs, seasonsWithFinal, teamFinalSeasons, facts, reasons, athletes);
    }

    internal static async Task<HashSet<int>> LoadSeasonsWithFinalAsync(
        SaveDbContext context,
        List<int> seasons,
        CancellationToken cancellationToken)
    {
        return (await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => seasons.Contains(e.SourceSeasonNumber)
                && (e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                    || e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final))
            .Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
    }

    internal static CupTeamHistoryResponse MapTypeHistory(
        Guid saveId,
        string teamKey,
        SimulationKernel.Rules.RulesV1 rules,
        TypeHistoryRows rows)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rows);
        return CupTeamHistoryBuilder.Build(new CupTeamHistoryBuilder.Input(
            saveId,
            CupTeamKeys.TypeCup,
            teamKey,
            teamKey,
            rules.TypeCupMinTeamSize,
            MapTypeSelections(rows.Selections, rows.Reasons),
            MapTypeStandings(rows.Standings, rows.SeasonsWithFinal, rows.TeamFinalSeasons),
            MapTypeLegs(rows.Legs),
            [],
            rows.Facts,
            rows.Athletes));
    }

    internal static List<CupTeamHistoryBuilder.SelectionRow> MapTypeSelections(
        List<TypeCupSelectionEntity> selections,
        Dictionary<(int Season, int Athlete), string> reasons)
    {
        return selections.Select(s => new CupTeamHistoryBuilder.SelectionRow(
            s.SourceSeasonNumber, s.SaveAthleteId, s.SelectionRank,
            s.FinalRatingThousandths, s.BonusNormThousandths, s.PerformanceNormThousandths,
            s.FormNormThousandths, s.PrestigeNormThousandths,
            reasons.GetValueOrDefault((s.SourceSeasonNumber, s.SaveAthleteId)))).ToList();
    }

    internal static List<CupTeamHistoryBuilder.StandingRow> MapTypeStandings(
        List<TypeCupTeamStandingEntity> standings,
        HashSet<int> seasonsWithFinal,
        HashSet<int> teamFinalSeasons)
    {
        return standings.Select(s => new CupTeamHistoryBuilder.StandingRow(
            s.SourceSeasonNumber, s.TeamRank, s.TeamScoreThousandths, s.TeamBaseThousandths,
            s.GroupWins, s.RoundWins, ((TypeCupMedal)s.Medal).ToString(),
            s.TournamentPhase, s.QualificationGroup,
            IsHonourEligible: s.TournamentPhase != (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification,
            QualifiedForFinal: s.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final
                || (s.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification
                    && teamFinalSeasons.Contains(s.SourceSeasonNumber)),
            EliminatedInQualification: s.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification
                && seasonsWithFinal.Contains(s.SourceSeasonNumber)
                && !teamFinalSeasons.Contains(s.SourceSeasonNumber))).ToList();
    }

    internal static List<CupTeamHistoryBuilder.LegRow> MapTypeLegs(List<TypeCupTeamGroupStandingEntity> legs)
    {
        return legs.Select(l => new CupTeamHistoryBuilder.LegRow(
            l.SourceSeasonNumber, l.SaveAthleteId, l.GroupNumber, l.GroupRank,
            l.GroupScoreThousandths, l.BaseScoreThousandths, l.RoundWins,
            l.TournamentPhase, l.QualificationGroup)).ToList();
    }

    internal static Dictionary<(int Season, int Athlete), string> ReadReasons(List<CupSelectionReportEntity> reports, string teamKey)
    {
        ArgumentNullException.ThrowIfNull(reports);
        Dictionary<(int Season, int Athlete), string> reasons = [];
        foreach (CupSelectionReportEntity report in reports)
        {
            TypeCupSelectionReportDocument document;
            try
            {
                document = TypeCupSelectionReportDocument.FromStored(report.PayloadJson);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            TypeCupSelectionReportDocument.Team? team = document.Teams
                .SingleOrDefault(t => string.Equals(t.CreatureType, teamKey, StringComparison.Ordinal));
            if (team is null)
            {
                continue;
            }

            foreach (TypeCupSelectionReportDocument.Candidate candidate in team.Ranking.Where(c => c.SelectionRank > 0))
            {
                reasons[(report.SourceSeasonNumber, candidate.AthleteId)] = GetTypeCupSelectionReportHandler.ReasonFor(candidate);
            }
        }

        return reasons;
    }

    internal static string BuildJson(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ReportData data)
    {
        return CupTeamReportJson.Build(saveId, metadata, rules, seasonCount, data);
    }

    internal static string BuildMarkdown(
        Guid saveId,
        SaveMetadataEntity metadata,
        SimulationKernel.Rules.RulesV1 rules,
        int seasonCount,
        ReportData data)
    {
        return CupTeamReportMarkdown.Build(saveId, metadata, rules, seasonCount, data);
    }
}
