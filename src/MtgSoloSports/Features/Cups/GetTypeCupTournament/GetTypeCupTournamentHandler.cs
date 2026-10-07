using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;

namespace MtgSoloSports.Features.Cups.GetTypeCupTournament;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted Type
/// Cup tournament for a completed even source season (never resimulates):
/// draw, each qualification group result with qualified/eliminated teams and
/// legs, the 32 finalists, the fresh Final result and champion/podium.
/// Direct Finals (1-32 teams) return the single Final with no qualification
/// groups. Old single-field saves (phase 0) return as direct Finals without
/// manufacturing qualification history.
/// </summary>
public sealed class GetTypeCupTournamentHandler
{
    private readonly SaveStore _store;

    public GetTypeCupTournamentHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetTypeCupTournamentResponse> HandleAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (sourceSeasonNumber is < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(sourceSeasonNumber));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity source = await LoadSourceAsync(context, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, source, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SeasonEntity> LoadSourceAsync(
        SaveDbContext context,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        if (sourceSeasonNumber.HasValue)
        {
            SeasonEntity? explicitSeason = await context.Seasons
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.SeasonNumber == sourceSeasonNumber.Value, cancellationToken)
                .ConfigureAwait(false);
            if (explicitSeason is null)
            {
                throw new TypeCupTournamentNotFoundException(
                    $"Type Cup tournament for Season {sourceSeasonNumber.Value} has not been resolved yet.");
            }

            await EnsureTournamentAsync(context, explicitSeason, cancellationToken).ConfigureAwait(false);
            return explicitSeason;
        }

        List<TypeCupTeamStandingEntity> any = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                || e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (any.Count == 0)
        {
            throw new TypeCupTournamentNotFoundException("Type Cup tournament has not been resolved yet.");
        }

        int latestSeasonId = any.Max(e => e.SourceSeasonId);
        SeasonEntity? latest = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == latestSeasonId, cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            throw new InvalidOperationException("Type Cup tournament references an unknown season.");
        }

        return latest;
    }

    internal static async Task EnsureTournamentAsync(
        SaveDbContext context,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        bool exists = await context.TypeCupTeamStandings
            .AsNoTracking()
            .AnyAsync(e => e.SourceSeasonId == source.Id
                && (e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField
                    || e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Final),
                cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            throw new TypeCupTournamentNotFoundException(
                $"Type Cup tournament for Season {source.SeasonNumber} has not been resolved yet.");
        }
    }

    internal static async Task<GetTypeCupTournamentResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity source,
        CancellationToken cancellationToken)
    {
        var rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        List<TypeCupSelectionEntity> selection = await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTournamentDrawEntity> draws = await context.TypeCupTournamentDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<TypeCupTeamRoundEntity> rounds = await context.TypeCupTeamRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntity> honours = await context.Honours
            .AsNoTracking()
            .Where(e => e.SeasonId == source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await LoadNamesAsync(context, legs, cancellationToken).ConfigureAwait(false);

        bool hasLegacy = teams.Any(t => t.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField)
            || rounds.Any(r => r.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.LegacySingleField);
        if (hasLegacy)
        {
            return BuildLegacyResponse(saveId, source, teams, legs, rounds, names);
        }

        int teamCount = selection.Select(e => e.CreatureType).Distinct(StringComparer.Ordinal).Count();
        var plan = TypeCupTournamentPlan.BuildFromSelection(
            teamCount,
            selection.Select(e => e.CreatureType).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
            draws,
            rules);
        TypeCupTeamInvariants.ValidateTournamentPersisted(source, rounds, legs, teams, honours, rules, plan);
        return plan.IsDirectFinal
            ? BuildDirectResponse(saveId, source, plan, teams, legs, rounds, names)
            : BuildTournamentResponse(saveId, source, plan, draws, teams, legs, rounds, names);
    }

    private static async Task<Dictionary<int, string>> LoadNamesAsync(
        SaveDbContext context,
        List<TypeCupTeamGroupStandingEntity> legs,
        CancellationToken cancellationToken)
    {
        List<int> ids = legs.Select(l => l.SaveAthleteId).Distinct().ToList();
        return await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    private static GetTypeCupTournamentResponse BuildLegacyResponse(
        Guid saveId,
        SeasonEntity source,
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        List<TypeCupTeamRoundEntity> rounds,
        Dictionary<int, string> names)
    {
        var orderedTeams = teams.OrderBy(t => t.TeamRank).ToList();
        string checksum = RunTypeCupTeamHandler.ComputeChecksum(ToRanked(orderedTeams, legs));
        var final = BuildFinalResult(teams, legs, rounds, names, checksum);
        return new GetTypeCupTournamentResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            true,
            teams.Count,
            0,
            [],
            [],
            string.Empty,
            [],
            orderedTeams.Select(t => t.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList(),
            final,
            final.ChampionCreatureType,
            checksum);
    }

    private static GetTypeCupTournamentResponse BuildDirectResponse(
        Guid saveId,
        SeasonEntity source,
        TypeCupTournamentPlan.Plan plan,
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        List<TypeCupTeamRoundEntity> rounds,
        Dictionary<int, string> names)
    {
        int finalPhase = (int)TypeCupTournamentFormat.TournamentPhase.Final;
        var finalTeams = teams.Where(t => t.TournamentPhase == finalPhase).OrderBy(t => t.TeamRank).ToList();
        var finalLegs = legs.Where(l => l.TournamentPhase == finalPhase).ToList();
        var finalRounds = rounds.Where(r => r.TournamentPhase == finalPhase).ToList();
        string checksum = RunTypeCupTeamHandler.ComputeChecksum(ToRanked(finalTeams, finalLegs));
        var final = BuildFinalResult(finalTeams, finalLegs, finalRounds, names, checksum);
        return new GetTypeCupTournamentResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            true,
            plan.TeamCount,
            0,
            [],
            [],
            string.Empty,
            [],
            finalTeams.Select(t => t.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList(),
            final,
            final.ChampionCreatureType,
            checksum);
    }

    private static GetTypeCupTournamentResponse BuildTournamentResponse(
        Guid saveId,
        SeasonEntity source,
        TypeCupTournamentPlan.Plan plan,
        List<TypeCupTournamentDrawEntity> draws,
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        List<TypeCupTeamRoundEntity> rounds,
        Dictionary<int, string> names)
    {
        string drawChecksum = draws.Count > 0 ? draws.First().DrawChecksum : string.Empty;
        List<TypeCupTournamentQualificationGroup> groups = BuildQualGroups(plan, teams, legs, names);
        int finalPhase = (int)TypeCupTournamentFormat.TournamentPhase.Final;
        var finalTeams = teams.Where(t => t.TournamentPhase == finalPhase).OrderBy(t => t.TeamRank).ToList();
        var finalLegs = legs.Where(l => l.TournamentPhase == finalPhase).ToList();
        var finalRounds = rounds.Where(r => r.TournamentPhase == finalPhase).ToList();
        string finalChecksum = RunTypeCupTeamHandler.ComputeChecksum(ToRanked(finalTeams, finalLegs));
        var final = BuildFinalResult(finalTeams, finalLegs, finalRounds, names, finalChecksum);
        List<string> finalists = finalTeams.Select(t => t.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList();
        return new GetTypeCupTournamentResponse(
            saveId,
            source.SeasonNumber,
            source.Id,
            false,
            plan.TeamCount,
            plan.QualificationGroupCount,
            plan.QualificationStages.Select(s => s.GroupSize).ToList(),
            plan.QualificationStages.Select(s => s.FinalPlaces).ToList(),
            drawChecksum,
            groups,
            finalists,
            final,
            final.ChampionCreatureType,
            finalChecksum);
    }

    private static List<TypeCupTournamentQualificationGroup> BuildQualGroups(
        TypeCupTournamentPlan.Plan plan,
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        Dictionary<int, string> names)
    {
        int qualPhase = (int)TypeCupTournamentFormat.TournamentPhase.Qualification;
        List<TypeCupTournamentQualificationGroup> groups = new(plan.QualificationGroupCount);
        foreach (var stage in plan.QualificationStages.OrderBy(s => s.QualificationGroup))
        {
            var stageTeams = teams.Where(t => t.TournamentPhase == qualPhase && t.QualificationGroup == stage.QualificationGroup).OrderBy(t => t.TeamRank).ToList();
            var stageLegs = legs.Where(l => l.TournamentPhase == qualPhase && l.QualificationGroup == stage.QualificationGroup).ToList();
            string checksum = RunTypeCupTeamHandler.ComputeChecksum(ToRanked(stageTeams, stageLegs));
            var qualified = stageTeams.OrderBy(t => t.TeamRank).Take(stage.FinalPlaces).Select(t => t.CreatureType).OrderBy(t => t, StringComparer.Ordinal).ToList();
            var qualifiedSet = qualified.ToHashSet(StringComparer.Ordinal);
            groups.Add(new TypeCupTournamentQualificationGroup(
                stage.QualificationGroup,
                stage.GroupSize,
                stage.FinalPlaces,
                checksum,
                stageTeams.Select(t => new TypeCupTournamentTeam(t.CreatureType, t.TeamRank, t.TeamScoreThousandths, t.TeamBaseThousandths, ((TypeCupMedal)t.Medal).ToString(), qualifiedSet.Contains(t.CreatureType))).ToList(),
                stageLegs.Select(l => new TypeCupTournamentLeg(l.SaveAthleteId, names.GetValueOrDefault(l.SaveAthleteId, $"Athlete {l.SaveAthleteId}"), l.CreatureType, l.SelectionRank, l.GroupNumber, l.GroupRank, l.GroupScoreThousandths, l.BaseScoreThousandths, qualifiedSet.Contains(l.CreatureType))).ToList(),
                qualified,
                stageTeams.Select(t => t.CreatureType).Where(t => !qualifiedSet.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList()));
        }

        return groups;
    }

    private static TypeCupTournamentFinalResult BuildFinalResult(
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs,
        List<TypeCupTeamRoundEntity> rounds,
        Dictionary<int, string> names,
        string checksum)
    {
        var orderedTeams = teams.OrderBy(t => t.TeamRank).ToList();
        var first = rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).First();
        var firstDoc = TypeCupTeamRoundPayloadDocument.FromStored(first.PayloadJson);
        var last = rounds.OrderBy(r => r.GroupNumber).ThenBy(r => r.RoundNumber).Last();
        var lastDoc = TypeCupTeamRoundPayloadDocument.FromStored(last.PayloadJson);
        var champion = orderedTeams.Single(t => t.TeamRank == 1);
        var finalists = orderedTeams.Select(t => t.CreatureType).ToHashSet(StringComparer.Ordinal);
        return new TypeCupTournamentFinalResult(
            orderedTeams.Count,
            checksum,
            firstDoc.RngBeforeState,
            firstDoc.RngBeforeStream,
            lastDoc.RngAfterState,
            lastDoc.RngAfterStream,
            champion.CreatureType,
            orderedTeams.Select(t => new TypeCupTournamentTeam(t.CreatureType, t.TeamRank, t.TeamScoreThousandths, t.TeamBaseThousandths, ((TypeCupMedal)t.Medal).ToString(), true)).ToList(),
            legs.Select(l => new TypeCupTournamentLeg(l.SaveAthleteId, names.GetValueOrDefault(l.SaveAthleteId, $"Athlete {l.SaveAthleteId}"), l.CreatureType, l.SelectionRank, l.GroupNumber, l.GroupRank, l.GroupScoreThousandths, l.BaseScoreThousandths, finalists.Contains(l.CreatureType))).ToList());
    }

    private static IReadOnlyList<SimulationKernel.Cups.TeamEvent.TeamRanked> ToRanked(
        List<TypeCupTeamStandingEntity> teams,
        List<TypeCupTeamGroupStandingEntity> legs)
    {
        // Rebuild minimal ranked inputs for checksum: checksum covers only
        // rank/type/score/base, so legs are not needed beyond team rows.
        // Construct via reflection-free minimal records.
        List<SimulationKernel.Cups.TeamEvent.TeamRanked> ranked = new(teams.Count);
        foreach (var team in teams.OrderBy(t => t.TeamRank))
        {
            ranked.Add(new SimulationKernel.Cups.TeamEvent.TeamRanked(0, team.CreatureType, team.TeamRank, team.TeamScoreThousandths, team.TeamBaseThousandths, team.GroupWins, team.RoundWins, [0], [0]));
        }

        return ranked;
    }
}
