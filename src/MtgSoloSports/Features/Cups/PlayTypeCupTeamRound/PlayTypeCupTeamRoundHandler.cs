using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;

/// <summary>
/// Endpoint -&gt; Handler direct call. Plays exactly the next Type Cup
/// tournament round in stage order (qualification groups in draw order, then
/// the Final) from the persisted state under the per-save lock and commits it
/// with the RNG state in one transaction (including rank-group leg tie-breaks
/// and qualification-stage team rankings when stages finish). Completing a
/// qualification stage persists its legs/teams plus nationality immediately so
/// eliminated teams retain history; the tournament Final completes the event
/// exactly as the one-shot run does.
/// </summary>
public sealed class PlayTypeCupTeamRoundHandler
{
    private readonly SaveStore _store;

    public PlayTypeCupTeamRoundHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<PlayTypeCupTeamRoundResponse> HandleAsync(
        Guid saveId,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PlayUnderLockAsync(saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<PlayTypeCupTeamRoundResponse> PlayUnderLockAsync(
        Guid saveId,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await RunTypeCupTeamHandler.EnsureTournamentDrawAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        RunTypeCupTeamHandler.TournamentState state = await RunTypeCupTeamHandler
            .LoadStateAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);

        // The Final field for a tournament is known only once every qual group
        // has completed standings persisted. Playing the first Final round
        // requires it; LoadState leaves it absent while quals are partial.
        await EnsureFinalFieldForNextRoundAsync(context, state, cancellationToken).ConfigureAwait(false);

        // For wildcard editions the Final must start past any seeded tie draw so
        // stepwise and one-shot runners agree. Untied wildcards leave RNG unchanged.
        Pcg32State rngBeforeNext = await WildcardRngBeforeNextAsync(context, state, cancellationToken).ConfigureAwait(false);

        (TypeCupTournamentPlan.StageKey stage, TypeCupTeamRoundPayloadDocument payload, Pcg32State after) =
            RunTypeCupTeamHandler.PlayNextRound(state, state.PlayedOrdered, rngBeforeNext);
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered =
            [.. state.PlayedOrdered, (stage, payload)];
        int total = state.Plan.TotalRounds;
        bool complete = ordered.Count == total;

        if (complete)
        {
            await RunTypeCupTeamHandler.CompleteAsync(context, state, ordered, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PersistSingleRoundAsync(context, state, stage, payload, after, ordered, cancellationToken).ConfigureAwait(false);
        }

        EventRoundView view = await EventRoundViews.BuildAsync(
            context,
            state.Source.SeasonNumber,
            PostseasonEvents.TypeCupTeam,
            payload.GroupNumber,
            payload.RoundNumber,
            payload.RulesVersion,
            payload.Checksum,
            new Pcg32State(payload.RngBeforeState, payload.RngBeforeStream),
            new Pcg32State(payload.RngAfterState, payload.RngAfterStream),
            payload.Placements,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new PlayTypeCupTeamRoundResponse(
            view,
            ordered.Count,
            total,
            complete,
            TournamentStageLabel(state, stage),
            stage.Phase,
            stage.QualificationGroup,
            payload.GroupNumber,
            payload.RoundNumber);
    }

    internal static string TournamentStageLabel(
        RunTypeCupTeamHandler.TournamentState state,
        TypeCupTournamentPlan.StageKey stage)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Plan.IsLegacy)
        {
            return "Single field";
        }

        if (state.Plan.IsDirectFinal)
        {
            return "Final";
        }

        if (stage.Phase == (int)TypeCupTournamentFormat.TournamentPhase.Final)
        {
            return "Final";
        }

        return $"Qualification Group {stage.QualificationGroup} of {state.Plan.QualificationGroupCount}";
    }

    private static async Task EnsureFinalFieldForNextRoundAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        CancellationToken cancellationToken)
    {
        if (!state.Plan.IsTournament)
        {
            return;
        }

        (TypeCupTournamentPlan.StageKey next, _, _) =
            TypeCupTournamentPlan.Cursor(state.PlayedOrdered.Count, state.Plan, state.Rules);
        if (next.Phase != (int)TypeCupTournamentFormat.TournamentPhase.Final)
        {
            return;
        }

        if (state.Fields.ContainsKey(next))
        {
            return;
        }

        // All qualification groups must be complete with persisted standings
        // before the Final can start; derive the 32 finalists from them.
        List<TypeCupTeamStandingEntity> qualTeams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == state.Source.Id
                && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> finalists = ResolveStepFinalists(state, qualTeams);
        state.Fields[next] = RunTypeCupTeamHandler.BuildFinalField(state, finalists);
    }

    internal static async Task<Pcg32State> WildcardRngBeforeNextAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Plan.IsTournament || !state.Plan.IsWildcardPolicy || state.Plan.WildcardCount == 0)
        {
            return state.Rng;
        }

        (TypeCupTournamentPlan.StageKey next, _, _) =
            TypeCupTournamentPlan.Cursor(state.PlayedOrdered.Count, state.Plan, state.Rules);
        if (next.Phase != (int)TypeCupTournamentFormat.TournamentPhase.Final)
        {
            return state.Rng;
        }

        // The wildcard tie draw (if the cutoff splits tied adjusted scores) sits
        // between the last qual ranking and the Final first round. Recompute it
        // deterministically from persisted standings plus the persisted RNG.
        List<TypeCupTeamStandingEntity> qualTeams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == state.Source.Id
                && e.TournamentPhase == (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (qualTeams.Count == 0)
        {
            return state.Rng;
        }

        // Only advance when every qualifier is complete; otherwise the next round
        // is still a qual round starting directly from state.Rng.
        int expectedQualTeams = state.Plan.QualificationStages.Sum(s => s.GroupSize);
        if (qualTeams.Count != expectedQualTeams)
        {
            return state.Rng;
        }

        return RunTypeCupTeamHandler.WildcardRngAfterFromPersisted(state, qualTeams, state.Rng);
    }

    internal static IReadOnlyList<string> ResolveStepFinalists(
        RunTypeCupTeamHandler.TournamentState state,
        List<TypeCupTeamStandingEntity> qualTeams)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(qualTeams);
        if (!state.Plan.IsWildcardPolicy)
        {
            Dictionary<int, IReadOnlyList<(string Team, int Rank)>> byGroup = qualTeams
                .GroupBy(e => e.QualificationGroup)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<(string Team, int Rank)>)g.OrderBy(t => t.TeamRank).Select(t => (t.CreatureType, t.TeamRank)).ToList());
            return TypeCupTournamentPlan.SelectFinalists(state.Plan, byGroup);
        }

        return RunTypeCupTeamHandler.ResolveFinalistsFromPersisted(state, qualTeams, state.Rng);
    }

    private static async Task PersistSingleRoundAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        TypeCupTeamRoundPayloadDocument payload,
        Pcg32State after,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        CancellationToken cancellationToken)
    {
        context.TypeCupTeamRounds.Add(new TypeCupTeamRoundEntity
        {
            SourceSeasonId = state.Source.Id,
            SourceSeasonNumber = state.Source.SeasonNumber,
            TournamentPhase = stage.Phase,
            QualificationGroup = stage.QualificationGroup,
            GroupNumber = payload.GroupNumber,
            RoundNumber = payload.RoundNumber,
            RulesVersion = payload.RulesVersion,
            RngBeforeState = unchecked((long)payload.RngBeforeState),
            RngBeforeStream = unchecked((long)payload.RngBeforeStream),
            RngAfterState = unchecked((long)payload.RngAfterState),
            RngAfterStream = unchecked((long)payload.RngAfterStream),
            PayloadJson = payload.ToStored(),
            PayloadChecksum = payload.Checksum,
        });
        context.ApplyRngState(after);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PersistCompletedQualificationStageAsync(context, state, stage, ordered, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistCompletedQualificationStageAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        TypeCupTournamentPlan.StageKey justPlayed,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        CancellationToken cancellationToken)
    {
        if (!IsCompletedQualStage(state, justPlayed, ordered))
        {
            return;
        }

        bool already = await context.TypeCupTeamStandings.AnyAsync(
            e => e.SourceSeasonId == state.Source.Id
                && e.TournamentPhase == justPlayed.Phase
                && e.QualificationGroup == justPlayed.QualificationGroup,
            cancellationToken).ConfigureAwait(false);
        if (already)
        {
            return;
        }

        await PersistQualStandingsAsync(context, state, justPlayed, ordered, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsCompletedQualStage(
        RunTypeCupTeamHandler.TournamentState state,
        TypeCupTournamentPlan.StageKey justPlayed,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered)
    {
        if (justPlayed.Phase != (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
        {
            return false;
        }

        int perStage = state.Rules.TypeCupMinTeamSize * state.Rules.TypeCupGroupRounds;
        int inStage = ordered.Count(o => o.Key.Phase == justPlayed.Phase && o.Key.QualificationGroup == justPlayed.QualificationGroup);
        return inStage == perStage;
    }

    private static async Task PersistQualStandingsAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        TypeCupTournamentPlan.StageKey justPlayed,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        CancellationToken cancellationToken)
    {
        List<TypeCupTeamRoundPayloadDocument> stagePayloads = ordered
            .Where(o => o.Key.Phase == justPlayed.Phase && o.Key.QualificationGroup == justPlayed.QualificationGroup)
            .Select(o => o.Payload)
            .ToList();
        (IReadOnlyList<TeamEvent.TeamLegRanked> legs, IReadOnlyList<TeamEvent.TeamRanked> teams, Pcg32State _) =
            RunTypeCupTeamHandler.RankStage(state, justPlayed, stagePayloads);
        await PersistQualRowsAsync(context, state, justPlayed, legs, teams, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistQualRowsAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        TypeCupTournamentPlan.StageKey justPlayed,
        IReadOnlyList<TeamEvent.TeamLegRanked> legs,
        IReadOnlyList<TeamEvent.TeamRanked> teams,
        CancellationToken cancellationToken)
    {
        Dictionary<int, TypeCupSelectionEntity> selectionByAthlete = (await context.TypeCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == state.Source.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToDictionary(e => e.SaveAthleteId);
        foreach (TeamEvent.TeamLegRanked leg in legs)
        {
            RunTypeCupTeamHandler.PersistStageLeg(context, state.Source, justPlayed, leg, selectionByAthlete);
        }

        foreach (TeamEvent.TeamRanked team in teams)
        {
            context.TypeCupTeamStandings.Add(new TypeCupTeamStandingEntity
            {
                SourceSeasonId = state.Source.Id,
                SourceSeasonNumber = state.Source.SeasonNumber,
                TournamentPhase = justPlayed.Phase,
                QualificationGroup = justPlayed.QualificationGroup,
                CreatureType = team.TeamName,
                TeamRank = team.TeamRank,
                TeamScoreThousandths = team.TeamScoreThousandths,
                TeamBaseThousandths = team.TeamBaseThousandths,
                GroupWins = team.GroupWins,
                RoundWins = team.RoundWins,
                GroupPlaceCountsJson = System.Text.Json.JsonSerializer.Serialize(team.GroupPlaceCounts),
                RoundPlaceCountsJson = System.Text.Json.JsonSerializer.Serialize(team.RoundPlaceCounts),
                Medal = (int)RunTypeCupTeam.TypeCupMedal.None,
            });
        }

        await RunTypeCupTeamHandler.ApplyStageNationalityAsync(context, state, justPlayed, legs, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
