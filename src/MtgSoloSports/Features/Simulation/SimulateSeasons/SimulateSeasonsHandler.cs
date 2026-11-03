using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Features.Reports.GoogleDrivePublishing;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Seasons.StartNextSeason;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Features.Simulation.SimulateSeasons;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Simulates an explicit
/// number of seasons with the identical sporting kernel, RNG consumption and
/// state transitions as manual round-by-round progression plus the canonical
/// postseason chain (Chaos Cup field plus tournament Final for eligible seasons;
/// inaugural movement for Season 1; automatic movement,
/// qualifier, rebalancing, alternating post-season Cup, then next-season start).
/// Odd seasons run the Color Cup (selection, individual, team); even seasons run
/// the Type Cup (selection, team). Chaos and Cups use the completed source season plus
/// current-season effective bonus values before season aging, in the same order
/// and RNG sequence as manual <c>AdvanceToNextEvent</c> progression.
///
/// Fast-mode guarantees:
/// <list type="bullet">
/// <item>same kernel: every step delegates to the focused
/// <c>*UnderLockAsync</c> operation used by manual play and
/// <c>AdvanceToNextEvent</c> (global-stage completion, inaugural/automatic
/// movement, qualifier, rebalancing, Cup selection/events, next-season start)
/// in the same canonical order;</item>
/// <item>no animation DTOs: inner detailed responses are discarded; the bulk
/// response carries only counts, cursors and RNG boundaries;</item>
/// <item>safe boundaries: no outer transaction. Each global stage and each
/// postseason step commits in its own transaction. Cancellation is observed
/// only between completed steps, so cancellation/failure never exposes a
/// partial league stage, partial qualifier or partial movement;</item>
/// <item>progress: <see cref="SimulateSeasonsResponse.Progress"/> exposes
/// requested/completed seasons, completed stages/postseason steps and the
/// start/end season numbers for a later UI indicator;</item>
/// <item>bounds: <see cref="MaxSeasonsPerRequest"/> caps one HTTP call while
/// preserving hundreds-of-seasons capability across calls.</item>
/// </list>
/// Holds one per-save lock for the whole bulk run; read-only status queries
/// never lock.
/// </summary>
public sealed class SimulateSeasonsHandler
{
    /// <summary>
    /// Upper request bound for one HTTP call. Hundreds of seasons stay
    /// possible in one call; thousands remain possible across repeated calls.
    /// </summary>
    public const int MaxSeasonsPerRequest = 500;

    private readonly SaveStore _store;
    private readonly DriveAutoPublishNotifier? _autoPublish;

    public SimulateSeasonsHandler(SaveStore store, DriveAutoPublishNotifier? autoPublish = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _autoPublish = autoPublish;
    }

    public async Task<SimulateSeasonsResponse> HandleAsync(
        Guid saveId,
        SimulateSeasonsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (request is null)
        {
            throw new ArgumentException("Request body is required.", nameof(request));
        }

        ValidateSeasons(request.Seasons);

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SimulateSeasonsResponse response;
        try
        {
            response = await SimulateUnderLockAsync(saveId, request.Seasons, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();

            // Bulk/fast simulation commits dozens of durable global stages.
            // The checkpoint note runs even when the bulk request is
            // cancelled or fails after intermediate commits, so the latest
            // durable state is eventually published. Coalesced to the newest
            // stable snapshot, never an older report overwriting a newer one.
            // Uses an unlinked token so client disconnect cannot lose the note.
            // Never throws, so it cannot mask the original sporting outcome.
            if (_autoPublish is not null)
            {
                await _autoPublish.NotifyStageCheckpointAsync(saveId, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return response;
    }

    public static void ValidateSeasons(int seasons)
    {
        if (seasons < 1 || seasons > MaxSeasonsPerRequest)
        {
            throw new ArgumentException(
                $"Seasons must be between 1 and {MaxSeasonsPerRequest}, was {seasons}.",
                nameof(seasons));
        }
    }

    internal async Task<SimulateSeasonsResponse> SimulateUnderLockAsync(
        Guid saveId,
        int seasonsRequested,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);

        int startSeason = await LoadCurrentSeasonNumberAsync(saveId, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = await LoadRngAsync(saveId, cancellationToken).ConfigureAwait(false);

        BulkStepHandlers handlers = CreateHandlers();
        BulkCounters counters = await RunLifecycleLoopAsync(saveId, seasonsRequested, handlers, cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(saveId, seasonsRequested, startSeason, rngBefore, counters, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record BulkStepHandlers(
        CompleteStageForAllLeaguesHandler BulkStages,
        Cups.SelectChaosCupField.SelectChaosCupFieldHandler SelectChaos,
        Cups.CompleteChaosCup.CompleteChaosCupHandler RunChaos,
        CreateInauguralSuperleagueHandler Inaugural,
        ResolveAutomaticMovementHandler Automatic,
        RunAllQualifiersHandler Qualifier,
        RebalanceFeedersHandler Rebalance,
        SelectColorCupTeamsHandler SelectColorCup,
        RunColorCupIndividualHandler RunColorCupIndividual,
        RunColorCupTeamHandler RunColorCupTeam,
        SelectTypeCupTeamsHandler SelectTypeCup,
        RunTypeCupTeamHandler RunTypeCupTeam,
        StartNextSeasonHandler Starter);

    internal sealed record BulkCounters(int SeasonsCompleted, int StagesCompleted, int PostseasonSteps);

    internal BulkStepHandlers CreateHandlers()
    {
        // The bulk global-stage path reuses the focused operation including
        // its post-commit checkpoint note, so a cancelled bulk request still
        // leaves the latest committed stage durably requested. Non-stage
        // postseason steps are covered by the per-iteration note in the loop.
        return new BulkStepHandlers(
            new CompleteStageForAllLeaguesHandler(_store, _autoPublish),
            new Cups.SelectChaosCupField.SelectChaosCupFieldHandler(_store),
            new Cups.CompleteChaosCup.CompleteChaosCupHandler(_store),
            new CreateInauguralSuperleagueHandler(_store),
            new ResolveAutomaticMovementHandler(_store),
            new RunAllQualifiersHandler(_store),
            new RebalanceFeedersHandler(_store),
            new SelectColorCupTeamsHandler(_store),
            new RunColorCupIndividualHandler(_store),
            new RunColorCupTeamHandler(_store),
            new SelectTypeCupTeamsHandler(_store),
            new RunTypeCupTeamHandler(_store),
            new StartNextSeasonHandler(_store, _autoPublish));
    }

    internal async Task<BulkCounters> RunLifecycleLoopAsync(
        Guid saveId,
        int seasonsRequested,
        BulkStepHandlers handlers,
        CancellationToken cancellationToken)
    {
        int seasonsCompleted = 0;
        int stagesCompleted = 0;
        int postseasonSteps = 0;

        // Each season needs at most 32 global stages plus at most 10 postseason
        // steps (Chaos selection, Chaos run, movement, qualifier, rebalance, up to 3 Cup steps, start);
        // bound total iterations so corrupted state cannot spin forever.
        int maxSteps = checked((seasonsRequested * 45) + 45);
        int steps = 0;

        while (seasonsCompleted < seasonsRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureStepBudget(steps, maxSteps, seasonsRequested);
            steps = checked(steps + 1);

            (bool seasonDone, bool stageDone) = await ExecuteNextLifecycleStepAsync(
                saveId, handlers, cancellationToken).ConfigureAwait(false);

            if (stageDone)
            {
                stagesCompleted = checked(stagesCompleted + 1);
            }
            else
            {
                postseasonSteps = checked(postseasonSteps + 1);
            }

            if (seasonDone)
            {
                seasonsCompleted = checked(seasonsCompleted + 1);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        return new BulkCounters(seasonsCompleted, stagesCompleted, postseasonSteps);
    }

    internal static void EnsureStepBudget(int steps, int maxSteps, int seasonsRequested)
    {
        if (steps >= maxSteps)
        {
            throw new InvalidOperationException(
                $"Simulation exceeded {maxSteps} lifecycle steps without completing {seasonsRequested} seasons; sporting state may be corrupt.");
        }
    }

    internal async Task<(bool SeasonDone, bool StageDone)> ExecuteNextLifecycleStepAsync(
        Guid saveId,
        BulkStepHandlers handlers,
        CancellationToken cancellationToken)
    {
        GetSeasonStatusResponse status = await LoadStatusAsync(saveId, cancellationToken).ConfigureAwait(false);
        if (status.LegalNextActions.Count != 1)
        {
            throw new SimulateSeasonsConflictException(
                $"Save has {status.LegalNextActions.Count} legal next actions; exactly one is required to simulate.");
        }

        string action = status.LegalNextActions[0];
        return await ExecuteActionAsync(saveId, action, status, handlers, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<(bool SeasonDone, bool StageDone)> ExecuteActionAsync(
        Guid saveId,
        string action,
        GetSeasonStatusResponse status,
        BulkStepHandlers handlers,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(handlers);
        if (await TryExecuteChaosAsync(saveId, action, status, handlers, cancellationToken).ConfigureAwait(false) is (bool, bool) chaos)
        {
            return chaos;
        }

        if (await TryExecutePostseasonAsync(saveId, action, status, handlers, cancellationToken).ConfigureAwait(false) is (bool, bool) post)
        {
            return post;
        }

        switch (action)
        {
            case SeasonLifecycleActions.CompleteNextGlobalStage:
                _ = await handlers.BulkStages
                    .CompleteGlobalStageUnderLockAsync(saveId, cancellationToken)
                    .ConfigureAwait(false);
                return (false, true);
            case SeasonLifecycleActions.StartNextSeason:
                _ = await handlers.Starter.StartUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
                return (true, false);
            default:
                throw new SimulateSeasonsConflictException($"Unknown lifecycle action '{action}'.");
        }
    }

    internal static void EnsureSource(GetSeasonStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.SourceSeasonNumber is null)
        {
            throw new SimulateSeasonsConflictException("Cup step requires a completed source season.");
        }
    }

    internal static async Task<(bool SeasonDone, bool StageDone)?> TryExecuteChaosAsync(
        Guid saveId,
        string action,
        GetSeasonStatusResponse status,
        BulkStepHandlers handlers,
        CancellationToken cancellationToken)
    {
        if (string.Equals(action, SeasonLifecycleActions.SelectChaosCup, StringComparison.Ordinal))
        {
            EnsureSource(status);
            _ = await handlers.SelectChaos
                .SelectUnderLockAsync(saveId, status.SourceSeasonNumber, cancellationToken)
                .ConfigureAwait(false);
            return (false, false);
        }

        if (string.Equals(action, SeasonLifecycleActions.RunChaosCup, StringComparison.Ordinal))
        {
            EnsureSource(status);
            _ = await handlers.RunChaos
                .CompleteCupUnderLockAsync(saveId, status.SourceSeasonNumber, cancellationToken)
                .ConfigureAwait(false);
            return (false, false);
        }

        return null;
    }

    internal static async Task<(bool SeasonDone, bool StageDone)?> TryExecutePostseasonAsync(
        Guid saveId,
        string action,
        GetSeasonStatusResponse status,
        BulkStepHandlers handlers,
        CancellationToken cancellationToken)
    {
        if (string.Equals(action, SeasonLifecycleActions.ResolveInauguralMovement, StringComparison.Ordinal))
        {
            _ = await handlers.Inaugural.CreateUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
            return (false, false);
        }

        if (string.Equals(action, SeasonLifecycleActions.ResolveAutomaticMovement, StringComparison.Ordinal))
        {
            _ = await handlers.Automatic.ResolveUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
            return (false, false);
        }

        if (string.Equals(action, SeasonLifecycleActions.RunQualifier, StringComparison.Ordinal))
        {
            _ = await handlers.Qualifier.RunRemainingUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
            return (false, false);
        }

        if (string.Equals(action, SeasonLifecycleActions.RebalanceFeeders, StringComparison.Ordinal))
        {
            _ = await handlers.Rebalance.RebalanceUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
            return (false, false);
        }

        if (await TryExecuteCupsAsync(saveId, action, status, handlers, cancellationToken).ConfigureAwait(false) is (bool, bool) cups)
        {
            return cups;
        }

        return null;
    }

    internal static async Task<(bool SeasonDone, bool StageDone)?> TryExecuteCupsAsync(
        Guid saveId,
        string action,
        GetSeasonStatusResponse status,
        BulkStepHandlers handlers,
        CancellationToken cancellationToken)
    {
        if (string.Equals(action, SeasonLifecycleActions.SelectColorCup, StringComparison.Ordinal))
        {
            EnsureSource(status);
            _ = await handlers.SelectColorCup
                .SelectUnderLockAsync(saveId, status.SourceSeasonNumber, cancellationToken)
                .ConfigureAwait(false);
            return (false, false);
        }

        if (string.Equals(action, SeasonLifecycleActions.RunColorCupIndividual, StringComparison.Ordinal))
        {
            EnsureSource(status);
            _ = await handlers.RunColorCupIndividual
                .RunUnderLockAsync(saveId, status.SourceSeasonNumber, cancellationToken)
                .ConfigureAwait(false);
            return (false, false);
        }

        if (string.Equals(action, SeasonLifecycleActions.RunColorCupTeam, StringComparison.Ordinal))
        {
            EnsureSource(status);
            _ = await handlers.RunColorCupTeam
                .RunUnderLockAsync(saveId, status.SourceSeasonNumber, cancellationToken)
                .ConfigureAwait(false);
            return (false, false);
        }

        if (string.Equals(action, SeasonLifecycleActions.SelectTypeCup, StringComparison.Ordinal))
        {
            EnsureSource(status);
            _ = await handlers.SelectTypeCup
                .SelectUnderLockAsync(saveId, status.SourceSeasonNumber, cancellationToken)
                .ConfigureAwait(false);
            return (false, false);
        }

        if (string.Equals(action, SeasonLifecycleActions.RunTypeCupTeam, StringComparison.Ordinal))
        {
            EnsureSource(status);
            _ = await handlers.RunTypeCupTeam
                .RunUnderLockAsync(saveId, status.SourceSeasonNumber, cancellationToken)
                .ConfigureAwait(false);
            return (false, false);
        }

        return null;
    }

    internal async Task<SimulateSeasonsResponse> BuildResponseAsync(
        Guid saveId,
        int seasonsRequested,
        int startSeason,
        Pcg32State rngBefore,
        BulkCounters counters,
        CancellationToken cancellationToken)
    {
        GetSeasonStatusResponse after = await LoadStatusAsync(saveId, cancellationToken).ConfigureAwait(false);
        Pcg32State rngAfter = await LoadRngAsync(saveId, cancellationToken).ConfigureAwait(false);

        SimulateSeasonsProgress progress = new(
            seasonsRequested,
            counters.SeasonsCompleted,
            counters.StagesCompleted,
            counters.PostseasonSteps,
            startSeason,
            after.CurrentSeasonNumber);

        return new SimulateSeasonsResponse(
            saveId,
            seasonsRequested,
            counters.SeasonsCompleted,
            counters.StagesCompleted,
            counters.PostseasonSteps,
            startSeason,
            after.CurrentSeasonNumber,
            after.GlobalStage,
            after.ComputedPhase,
            after.IsCurrentSeasonComplete,
            rngBefore.State,
            rngBefore.Stream,
            rngAfter.State,
            rngAfter.Stream,
            progress);
    }

    internal async Task<int> LoadCurrentSeasonNumberAsync(Guid saveId, CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        return metadata.CurrentSeason;
    }

    internal async Task<Pcg32State> LoadRngAsync(Guid saveId, CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        RngStateEntity row = await context.RngStates
            .AsNoTracking()
            .SingleAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        return row.ToState();
    }

    internal async Task<GetSeasonStatusResponse> LoadStatusAsync(Guid saveId, CancellationToken cancellationToken)
    {
        GetSeasonStatusHandler status = new(_store);
        return await status.HandleAsync(saveId, cancellationToken).ConfigureAwait(false);
    }
}
