using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Features.Cups.GetTypeCupSelectionReport;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Cups.GetTypeCupTeamHistory;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one creature-type
/// team's history across every Type Cup it fielded a squad for: squads with
/// the stored reason each member represents the type, team results and group
/// legs. Read-only. The team key is the creature type exactly as stored.
/// Throws <see cref="CupTeamHistoryNotFoundException"/> (404) for a type that
/// never fielded a team and aborts on corrupt squads or legs. Editions
/// selected before reports were stored carry no reason.
/// </summary>
public sealed class GetTypeCupTeamHistoryHandler
{
    private readonly SaveStore _store;

    public GetTypeCupTeamHistoryHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<CupTeamHistoryResponse> HandleAsync(
        Guid saveId,
        string teamKey,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (string.IsNullOrWhiteSpace(teamKey))
        {
            throw new CupTeamHistoryNotFoundException("A Type Cup team needs a creature type.");
        }

        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        (List<TypeCupSelectionEntity> fields, List<TypeCupSelectionEntity> selections, List<TypeCupTeamStandingEntity> standings, List<TypeCupTeamGroupStandingEntity> legs) =
            await LoadTeamRowsAsync(context, teamKey, cancellationToken).ConfigureAwait(false);

        List<int> seasons = selections.Select(s => s.SourceSeasonNumber).Distinct().ToList();
        HashSet<int> seasonsWithFinal = await LoadSeasonsWithFinalAsync(context, seasons, cancellationToken).ConfigureAwait(false);
        HashSet<int> teamFinalSeasons = TeamFinalSeasons(standings);
        Dictionary<int, CupTeamHistoryBuilder.SeasonFacts> facts = await LoadFactsAsync(context, fields, seasons, cancellationToken).ConfigureAwait(false);

        Dictionary<(int Season, int Athlete), string> reasons =
            await LoadReasonsAsync(context, selections, teamKey, cancellationToken).ConfigureAwait(false);
        List<int> athleteIds = selections.Select(s => s.SaveAthleteId).Distinct().ToList();
        Dictionary<int, SaveAthleteEntity> athletes = await context.SaveAthletes
            .AsNoTracking().Where(e => athleteIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);

        return CupTeamHistoryBuilder.Build(new CupTeamHistoryBuilder.Input(
            saveId,
            CupTeamKeys.TypeCup,
            teamKey,
            teamKey,
            rules.TypeCupMinTeamSize,
            MapSelections(selections, reasons),
            MapStandings(standings, seasonsWithFinal, teamFinalSeasons),
            MapLegs(legs),
            [],
            facts,
            athletes));
    }

    private static bool IsTeam(string creatureType, string teamKey) =>
        string.Equals(creatureType, teamKey, StringComparison.Ordinal);

    private static async Task<HashSet<int>> LoadSeasonsWithFinalAsync(
        SaveDbContext context,
        List<int> seasons,
        CancellationToken cancellationToken)
    {
        // Seasons with any persisted Final (or legacy) result anywhere in the
        // save: a qualification table in such a season is decided. Whether
        // this team was eliminated or advanced depends on its own rows.
        return (await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => seasons.Contains(e.SourceSeasonNumber)
                && (e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                    || e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final))
            .Select(e => e.SourceSeasonNumber)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
    }

    private static HashSet<int> TeamFinalSeasons(List<TypeCupTeamStandingEntity> standings)
    {
        return standings
            .Where(e => e.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final)
            .Select(e => e.SourceSeasonNumber)
            .ToHashSet();
    }

    private static async Task<Dictionary<int, CupTeamHistoryBuilder.SeasonFacts>> LoadFactsAsync(
        SaveDbContext context,
        List<TypeCupSelectionEntity> fields,
        List<int> seasons,
        CancellationToken cancellationToken)
    {
        List<int> roundSeasons = await context.TypeCupTeamRounds
            .AsNoTracking().Where(e => seasons.Contains(e.SourceSeasonNumber))
            .Select(e => e.SourceSeasonNumber).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        return seasons.ToDictionary(
            season => season,
            season => new CupTeamHistoryBuilder.SeasonFacts(
                fields.Where(f => f.SourceSeasonNumber == season).Select(f => f.CreatureType).Distinct(StringComparer.Ordinal).Count(),
                roundSeasons.Contains(season),
                IndividualComplete: true));
    }

    private static async Task<Dictionary<(int Season, int Athlete), string>> LoadReasonsAsync(
        SaveDbContext context,
        List<TypeCupSelectionEntity> selections,
        string teamKey,
        CancellationToken cancellationToken)
    {
        List<int> seasonIds = selections.Select(s => s.SourceSeasonId).Distinct().ToList();
        List<CupSelectionReportEntity> reports = await context.CupSelectionReports
            .AsNoTracking().Where(e => seasonIds.Contains(e.SourceSeasonId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ReadReasons(reports, teamKey);
    }

    private static async Task<(List<TypeCupSelectionEntity> Fields, List<TypeCupSelectionEntity> Selections, List<TypeCupTeamStandingEntity> Standings, List<TypeCupTeamGroupStandingEntity> Legs)> LoadTeamRowsAsync(
        SaveDbContext context,
        string teamKey,
        CancellationToken cancellationToken)
    {
        List<TypeCupSelectionEntity> fields = await context.TypeCupSelections
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<TypeCupSelectionEntity> selections = fields.Where(e => IsTeam(e.CreatureType, teamKey)).ToList();
        // Tournament-stage aware: preserve every stage for this team (a
        // finalist keeps both its qualification table and the Final) so team
        // history never collapses stages. The stage-aware builder selects the
        // official result per stage instead of SingleOrDefault per season.
        List<TypeCupTeamStandingEntity> allStandings = (await context.TypeCupTeamStandings
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(e => IsTeam(e.CreatureType, teamKey)).ToList();
        List<TypeCupTeamGroupStandingEntity> allLegs = (await context.TypeCupTeamGroupStandings
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            .Where(e => IsTeam(e.CreatureType, teamKey)).ToList();
        return (fields, selections, allStandings, allLegs);
    }

    /// <summary>The stored reason of each member of this team, per edition that has a report.</summary>
    internal static Dictionary<(int Season, int Athlete), string> ReadReasons(
        List<CupSelectionReportEntity> reports,
        string teamKey)
    {
        ArgumentNullException.ThrowIfNull(reports);
        Dictionary<(int Season, int Athlete), string> reasons = [];
        foreach (CupSelectionReportEntity report in reports)
        {
            TypeCupSelectionReportDocument document = TypeCupSelectionReportDocument.FromStored(report.PayloadJson);
            TypeCupSelectionReportDocument.Team? team = document.Teams
                .SingleOrDefault(t => IsTeam(t.CreatureType, teamKey));
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

    private static List<CupTeamHistoryBuilder.SelectionRow> MapSelections(
        List<TypeCupSelectionEntity> selections,
        Dictionary<(int Season, int Athlete), string> reasons)
    {
        return selections.Select(s => new CupTeamHistoryBuilder.SelectionRow(
            s.SourceSeasonNumber,
            s.SaveAthleteId,
            s.SelectionRank,
            s.FinalRatingThousandths,
            s.BonusNormThousandths,
            s.PerformanceNormThousandths,
            s.FormNormThousandths,
            s.PrestigeNormThousandths,
            reasons.GetValueOrDefault((s.SourceSeasonNumber, s.SaveAthleteId)))).ToList();
    }

    private static List<CupTeamHistoryBuilder.StandingRow> MapStandings(
        List<TypeCupTeamStandingEntity> standings,
        HashSet<int> seasonsWithFinal,
        HashSet<int> teamFinalSeasons)
    {
        return standings.Select(s => new CupTeamHistoryBuilder.StandingRow(
            s.SourceSeasonNumber,
            s.TeamRank,
            s.TeamScoreThousandths,
            s.TeamBaseThousandths,
            s.GroupWins,
            s.RoundWins,
            ((TypeCupMedal)s.Medal).ToString(),
            s.TournamentPhase,
            s.QualificationGroup,
            IsHonourEligible: s.TournamentPhase != (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification,
            QualifiedForFinal: s.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Final
                || (s.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification
                    && teamFinalSeasons.Contains(s.SourceSeasonNumber)),
            EliminatedInQualification: s.TournamentPhase == (int)SimulationKernel.Cups.TypeCupTournamentFormat.TournamentPhase.Qualification
                && seasonsWithFinal.Contains(s.SourceSeasonNumber)
                && !teamFinalSeasons.Contains(s.SourceSeasonNumber))).ToList();
    }

    private static List<CupTeamHistoryBuilder.LegRow> MapLegs(List<TypeCupTeamGroupStandingEntity> legs)
    {
        return legs.Select(l => new CupTeamHistoryBuilder.LegRow(
            l.SourceSeasonNumber,
            l.SaveAthleteId,
            l.GroupNumber,
            l.GroupRank,
            l.GroupScoreThousandths,
            l.BaseScoreThousandths,
            l.RoundWins,
            l.TournamentPhase,
            l.QualificationGroup)).ToList();
    }
}
