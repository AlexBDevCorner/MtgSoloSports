using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Simulation.GlobalStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Completes the current
/// global stage for every active league that has not yet completed it, in
/// canonical ascending league-id order, by delegating to the same underlying
/// <c>CompleteStageHandler.CompleteUnderLockAsync</c> round/stage operations
/// used by single-league completion. Bulk execution is therefore behaviorally
/// equivalent to sequential single-league completions in canonical order.
/// Holds one per-save lock for the whole global stage; each league commits in
/// its own stage-boundary transaction with RNG-after chaining via the save
/// row, and season finalization (Stage 32, all leagues) commits atomically in
/// the last league's transaction.
/// </summary>
public sealed class CompleteStageForAllLeaguesHandler
{
    private readonly SaveStore _store;
    private readonly DriveAutoPublishNotifier? _autoPublish;

    public CompleteStageForAllLeaguesHandler(SaveStore store, DriveAutoPublishNotifier? autoPublish = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _autoPublish = autoPublish;
    }

    public async Task<CompleteStageForAllLeaguesResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CompleteStageForAllLeaguesResponse response;
        try
        {
            response = await CompleteGlobalStageUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();

            // Post-commit durability: a global stage commits per league, so
            // cancellation or failure after an intermediate commit must still
            // leave a durable checkpoint note. Runs even on exception, uses
            // an unlinked token and never throws.
            if (_autoPublish is not null)
            {
                await _autoPublish.NotifyStageCheckpointAsync(saveId, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return response;
    }

    internal async Task<CompleteStageForAllLeaguesResponse> CompleteGlobalStageUnderLockAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
            GlobalPlan plan = await LoadPlanAsync(saveId, cancellationToken).ConfigureAwait(false);
            List<BulkLeagueStageResult> results = await CompletePendingAsync(saveId, plan, cancellationToken).ConfigureAwait(false);
            CompleteStageForAllLeaguesResponse response =
                await BuildResponseAsync(saveId, plan, results, cancellationToken).ConfigureAwait(false);
            return response;
        }
        finally
        {
            // Durable global-stage boundary commits per league. Cancellation
            // after the final league commit (ThrowIfCancellationRequested in
            // CompletePendingAsync or a cancelled BuildResponseAsync) must
            // still leave a checkpoint note while the app keeps running.
            // Duplicate-safe; uses an unlinked token and never throws.
            if (_autoPublish is not null)
            {
                await _autoPublish.NotifyStageCheckpointAsync(saveId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal sealed record GlobalPlan(
        int SeasonNumber,
        int TargetStage,
        List<int> PendingLeagueIds);

    internal async Task<GlobalPlan> LoadPlanAsync(Guid saveId, CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity season = await AdvanceRoundHandler.LoadSeasonAsync(context, metadata, cancellationToken).ConfigureAwait(false);
        GlobalStageGate.GlobalStageView global = await GlobalStageGate
            .LoadGlobalStageAsync(context, season, rules, cancellationToken)
            .ConfigureAwait(false);
        return BuildPlan(season, global);
    }

    internal static GlobalPlan BuildPlan(SeasonEntity season, GlobalStageGate.GlobalStageView global)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(global);
        if (global.IsSeasonComplete)
        {
            throw new CompleteStageForAllLeaguesConflictException(
                $"Season {season.SeasonNumber} is already complete; season completion is handled by a later slice.");
        }

        List<int> pending = global.Leagues
            .Where(l => !l.IsLeagueComplete && l.CurrentStage == global.CurrentStage)
            .OrderBy(l => l.LeagueId)
            .Select(l => l.LeagueId)
            .ToList();
        if (pending.Count == 0)
        {
            throw new CompleteStageForAllLeaguesConflictException(
                $"Global stage {global.CurrentStage} is already complete for every active league.");
        }

        return new GlobalPlan(season.SeasonNumber, global.CurrentStage, pending);
    }

    internal async Task<List<BulkLeagueStageResult>> CompletePendingAsync(
        Guid saveId,
        GlobalPlan plan,
        CancellationToken cancellationToken)
    {
        CompleteStageHandler single = new(_store);
        List<BulkLeagueStageResult> results = new(plan.PendingLeagueIds.Count);
        foreach (int leagueId in plan.PendingLeagueIds)
        {
            CompleteStageResponse completed = await single.CompleteUnderLockAsync(saveId, leagueId, cancellationToken).ConfigureAwait(false);
            if (completed.StageNumber != plan.TargetStage)
            {
                throw new InvalidOperationException(
                    $"Bulk completion expected stage {plan.TargetStage} for league {leagueId}, was {completed.StageNumber}.");
            }

            results.Add(new BulkLeagueStageResult(
                completed.LeagueId,
                completed.LeagueName,
                completed.StageNumber,
                completed.StageChecksum,
                completed.NextStageNumber));
            cancellationToken.ThrowIfCancellationRequested();
        }

        return results;
    }

    internal async Task<CompleteStageForAllLeaguesResponse> BuildResponseAsync(
        Guid saveId,
        GlobalPlan plan,
        List<BulkLeagueStageResult> results,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity season = await AdvanceRoundHandler.LoadSeasonAsync(context, metadata, cancellationToken).ConfigureAwait(false);
        GlobalStageGate.GlobalStageView after = await GlobalStageGate
            .LoadGlobalStageAsync(context, season, rules, cancellationToken)
            .ConfigureAwait(false);
        return new CompleteStageForAllLeaguesResponse(
            saveId,
            plan.SeasonNumber,
            plan.TargetStage,
            after.CurrentStage,
            after.IsSeasonComplete,
            results);
    }
}
