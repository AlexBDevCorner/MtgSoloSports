using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.History.EventRounds;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Cups.FinishTypeCupTeamStage;

/// <summary>
/// Endpoint -&gt; Handler direct call. Finishes the canonical Type Cup tournament
/// stage (one qualification group or the Final) atomically under the per-save
/// lock. Snapshots the canonical stage inside the lock, validates an optional
/// expected stage (stale UI must refresh instead of advancing the wrong stage),
/// then plays only the remaining rounds of that stage in canonical order. Never
/// crosses into the next group/Final: the loop breaks on stage change and the
/// whole stage commits in one transaction with the RNG state. Completing the
/// Final completes the event exactly as the one-shot run does.
/// </summary>
public sealed class FinishTypeCupTeamStageHandler
{
    private readonly SaveStore _store;

    public FinishTypeCupTeamStageHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<FinishTypeCupTeamStageResponse> HandleAsync(
        Guid saveId,
        int? expectedTournamentPhase = null,
        int? expectedQualificationGroup = null,
        int? sourceSeasonNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if ((expectedTournamentPhase is null) != (expectedQualificationGroup is null))
        {
            throw new ArgumentException("A stage scope needs both tournamentPhase and qualificationGroup.", nameof(expectedTournamentPhase));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FinishUnderLockAsync(saveId, expectedTournamentPhase, expectedQualificationGroup, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<FinishTypeCupTeamStageResponse> FinishUnderLockAsync(
        Guid saveId,
        int? expectedTournamentPhase,
        int? expectedQualificationGroup,
        int? sourceSeasonNumber,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RunTypeCupTeamHandler.EnsureTournamentDrawAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        RunTypeCupTeamHandler.TournamentState state = await RunTypeCupTeamHandler
            .LoadStateAsync(context, saveId, sourceSeasonNumber, cancellationToken).ConfigureAwait(false);
        TypeCupTournamentPlan.StageKey startStage = ResolveStartStage(state, expectedTournamentPhase, expectedQualificationGroup);
        int perStage = state.Rules.TypeCupMinTeamSize * state.Rules.TypeCupGroupRounds;
        int initialCount = state.PlayedOrdered.Count;
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> working = new(state.PlayedOrdered);
        (working, Pcg32State current) = await SimulateRemainingStageAsync(context, state, startStage, working, cancellationToken).ConfigureAwait(false);
        int playedThisCall = working.Count - initialCount;
        if (playedThisCall <= 0)
        {
            throw new RunTypeCupTeamConflictException(
                $"Type Cup stage phase {startStage.Phase} qual {startStage.QualificationGroup} is already complete; refresh and continue to the next stage.");
        }

        bool tournamentComplete = working.Count == state.Plan.TotalRounds;
        await PersistStageResultAsync(context, state, startStage, working, initialCount, current, tournamentComplete, cancellationToken).ConfigureAwait(false);
        FinishTypeCupTeamStageResponse response = await BuildResponseAsync(context, state, working, startStage, perStage, playedThisCall, tournamentComplete, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return response;
    }

    internal static TypeCupTournamentPlan.StageKey ResolveStartStage(
        RunTypeCupTeamHandler.TournamentState state,
        int? expectedTournamentPhase,
        int? expectedQualificationGroup)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.PlayedOrdered.Count >= state.Plan.TotalRounds)
        {
            throw new RunTypeCupTeamConflictException(
                $"Type Cup team event for Season {state.Source.SeasonNumber} has already been resolved.");
        }

        (TypeCupTournamentPlan.StageKey startStage, _, _) =
            TypeCupTournamentPlan.Cursor(state.PlayedOrdered.Count, state.Plan, state.Rules);
        if (expectedTournamentPhase is not null && expectedQualificationGroup is not null
            && (startStage.Phase != expectedTournamentPhase.Value || startStage.QualificationGroup != expectedQualificationGroup.Value))
        {
            throw new RunTypeCupTeamConflictException(
                $"Type Cup stage changed while finishing; expected phase {expectedTournamentPhase.Value} qual {expectedQualificationGroup.Value} but the canonical stage is phase {startStage.Phase} qual {startStage.QualificationGroup}. Refresh and try again.");
        }

        return startStage;
    }

    private static async Task<(List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> Working, Pcg32State Current)> SimulateRemainingStageAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        TypeCupTournamentPlan.StageKey startStage,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> working,
        CancellationToken cancellationToken)
    {
        Pcg32State current = state.Rng;
        current = await RunTypeCupTeamHandler.ApplyPersistedWildcardRngAsync(context, state, working, current, cancellationToken).ConfigureAwait(false);
        while (working.Count < state.Plan.TotalRounds)
        {
            (TypeCupTournamentPlan.StageKey cursor, _, _) =
                TypeCupTournamentPlan.Cursor(working.Count, state.Plan, state.Rules);
            if (cursor.Phase != startStage.Phase || cursor.QualificationGroup != startStage.QualificationGroup)
            {
                break;
            }

            (working, current) = PlayOneStageRound(state, working, current, startStage);
        }

        return (working, current);
    }

    private static (List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> Working, Pcg32State Current) PlayOneStageRound(
        RunTypeCupTeamHandler.TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> working,
        Pcg32State current,
        TypeCupTournamentPlan.StageKey startStage)
    {
        current = RunTypeCupTeamHandler.EnsureInMemoryFinalField(state, working, current);
        (TypeCupTournamentPlan.StageKey stage, TypeCupTeamRoundPayloadDocument payload, Pcg32State after) =
            RunTypeCupTeamHandler.PlayNextRound(state, working, current);
        if (stage.Phase != startStage.Phase || stage.QualificationGroup != startStage.QualificationGroup)
        {
            return (working, current);
        }

        working.Add((stage, payload));
        return (working, after);
    }

    private static async Task PersistStageResultAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        TypeCupTournamentPlan.StageKey startStage,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> working,
        int initialCount,
        Pcg32State current,
        bool tournamentComplete,
        CancellationToken cancellationToken)
    {
        if (tournamentComplete)
        {
            await RunTypeCupTeamHandler.CompleteAsync(context, state, working, cancellationToken).ConfigureAwait(false);
            return;
        }

        RunTypeCupTeamHandler.PersistTournamentRoundRows(context, state.Source, working, initialCount);
        context.ApplyRngState(current);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await PersistCompletedQualificationStageAsync(context, state, startStage, working, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FinishTypeCupTeamStageResponse> BuildResponseAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> working,
        TypeCupTournamentPlan.StageKey startStage,
        int perStage,
        int playedThisCall,
        bool tournamentComplete,
        CancellationToken cancellationToken)
    {
        (TypeCupTournamentPlan.StageKey lastStage, TypeCupTeamRoundPayloadDocument lastPayload) = working[^1];
        EventRoundView view = await EventRoundViews.BuildAsync(
            context,
            state.Source.SeasonNumber,
            PostseasonEvents.TypeCupTeam,
            lastPayload.GroupNumber,
            lastPayload.RoundNumber,
            lastPayload.RulesVersion,
            lastPayload.Checksum,
            new Pcg32State(lastPayload.RngBeforeState, lastPayload.RngBeforeStream),
            new Pcg32State(lastPayload.RngAfterState, lastPayload.RngAfterStream),
            lastPayload.Placements,
            cancellationToken,
            lastStage.Phase,
            lastStage.QualificationGroup).ConfigureAwait(false);
        int inStage = working.Count(o => o.Key.Phase == startStage.Phase && o.Key.QualificationGroup == startStage.QualificationGroup);
        return new FinishTypeCupTeamStageResponse(
            view,
            working.Count,
            state.Plan.TotalRounds,
            tournamentComplete,
            PlayTypeCupTeamRoundHandler.TournamentStageLabel(state, lastStage),
            lastStage.Phase,
            lastStage.QualificationGroup,
            lastPayload.GroupNumber,
            lastPayload.RoundNumber,
            inStage,
            perStage,
            playedThisCall);
    }

    private static async Task PersistCompletedQualificationStageAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
        List<(TypeCupTournamentPlan.StageKey Key, TypeCupTeamRoundPayloadDocument Payload)> ordered,
        CancellationToken cancellationToken)
    {
        if (stage.Phase != (int)TypeCupTournamentFormat.TournamentPhase.Qualification)
        {
            return;
        }

        int perStage = state.Rules.TypeCupMinTeamSize * state.Rules.TypeCupGroupRounds;
        int inStage = ordered.Count(o => o.Key.Phase == stage.Phase && o.Key.QualificationGroup == stage.QualificationGroup);
        if (inStage != perStage)
        {
            return;
        }

        bool already = await context.TypeCupTeamStandings.AnyAsync(
            e => e.SourceSeasonId == state.Source.Id
                && e.TournamentPhase == stage.Phase
                && e.QualificationGroup == stage.QualificationGroup,
            cancellationToken).ConfigureAwait(false);
        if (already)
        {
            return;
        }

        List<TypeCupTeamRoundPayloadDocument> stagePayloads = ordered
            .Where(o => o.Key.Phase == stage.Phase && o.Key.QualificationGroup == stage.QualificationGroup)
            .Select(o => o.Payload)
            .ToList();
        (IReadOnlyList<TeamEvent.TeamLegRanked> legs, IReadOnlyList<TeamEvent.TeamRanked> teams, Pcg32State _) =
            RunTypeCupTeamHandler.RankStage(state, stage, stagePayloads);
        await PersistQualStandingsRowsAsync(context, state, stage, legs, teams, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistQualStandingsRowsAsync(
        SaveDbContext context,
        RunTypeCupTeamHandler.TournamentState state,
        TypeCupTournamentPlan.StageKey stage,
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
            RunTypeCupTeamHandler.PersistStageLeg(context, state.Source, stage, leg, selectionByAthlete);
        }

        foreach (TeamEvent.TeamRanked team in teams)
        {
            context.TypeCupTeamStandings.Add(new TypeCupTeamStandingEntity
            {
                SourceSeasonId = state.Source.Id,
                SourceSeasonNumber = state.Source.SeasonNumber,
                TournamentPhase = stage.Phase,
                QualificationGroup = stage.QualificationGroup,
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

        await RunTypeCupTeamHandler.ApplyStageNationalityAsync(context, state, stage, legs, cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
