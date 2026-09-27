using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Simulation.GlobalStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Features.Simulation.CompleteSeason;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Fast-forwards the current
/// season by completing every remaining global stage with the identical
/// sporting kernel, RNG consumption and state transitions as sequential
/// manual <c>AdvanceRound</c> / <c>CompleteStage</c> /
/// <c>CompleteStageForAllLeagues</c> operations in canonical ascending
/// league-id order.
///
/// Fast-mode guarantees:
/// <list type="bullet">
/// <item>same kernel: delegates to
/// <c>CompleteStageForAllLeaguesHandler.CompleteGlobalStageUnderLockAsync</c>,
/// which itself delegates to <c>CompleteStageHandler</c> round/stage math;</item>
/// <item>no animation DTOs: the bulk response carries only counts, cursors and
/// RNG boundaries; per-round placements and per-stage standings stay in
/// persisted rows for replay queries;</item>
/// <item>safe boundaries: no outer transaction. Each global stage commits in
/// its own stage-boundary transactions (one per league stage plus the
/// terminal season-finalization commit). Cancellation is observed only between
/// completed global stages, so a cancelled call never exposes a partial
/// league stage (rounds without standings);</item>
/// <item>progress: <see cref="CompleteSeasonResponse.Progress"/> exposes
/// completed/total stages plus before/after cursors for a later UI indicator.</item>
/// </list>
/// Holds one per-save lock for the whole season; read-only queries never lock.
/// </summary>
public sealed class CompleteSeasonHandler
{
    private readonly SaveStore _store;

    public CompleteSeasonHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<CompleteSeasonResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SemaphoreSlim gate = AdvanceRoundSaveLock.Acquire(saveId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CompleteSeasonUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<CompleteSeasonResponse> CompleteSeasonUnderLockAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);

        (int seasonNumber, int globalBefore, Pcg32State rngBefore, int totalStages) =
            await LoadSeasonCursorAsync(saveId, cancellationToken).ConfigureAwait(false);

        int stagesCompleted = await CompleteRemainingStagesAsync(saveId, seasonNumber, cancellationToken).ConfigureAwait(false);

        return await BuildResponseAsync(saveId, seasonNumber, globalBefore, rngBefore, totalStages, stagesCompleted, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<int> CompleteRemainingStagesAsync(
        Guid saveId,
        int seasonNumber,
        CancellationToken cancellationToken)
    {
        CompleteStageForAllLeaguesHandler bulk = new(_store);
        int stagesCompleted = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteStageForAllLeaguesResponse? completed = await TryCompleteOneStageAsync(bulk, saveId, cancellationToken).ConfigureAwait(false);
            if (completed is null)
            {
                break;
            }

            EnsureSameSeason(completed, seasonNumber);
            stagesCompleted = checked(stagesCompleted + 1);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (stagesCompleted == 0)
        {
            throw new CompleteSeasonConflictException(
                $"Season {seasonNumber} is already complete; season completion is handled by the postseason chain.");
        }

        return stagesCompleted;
    }

    internal static async Task<CompleteStageForAllLeaguesResponse?> TryCompleteOneStageAsync(
        CompleteStageForAllLeaguesHandler bulk,
        Guid saveId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bulk);
        try
        {
            return await bulk.CompleteGlobalStageUnderLockAsync(saveId, cancellationToken).ConfigureAwait(false);
        }
        catch (CompleteStageForAllLeaguesConflictException)
        {
            return null;
        }
    }

    internal static void EnsureSameSeason(CompleteStageForAllLeaguesResponse completed, int seasonNumber)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (completed.SeasonNumber != seasonNumber)
        {
            throw new InvalidOperationException(
                $"Bulk completion crossed seasons ({seasonNumber} -> {completed.SeasonNumber}); seasons must complete one at a time.");
        }
    }

    internal async Task<CompleteSeasonResponse> BuildResponseAsync(
        Guid saveId,
        int seasonNumber,
        int globalBefore,
        Pcg32State rngBefore,
        int totalStages,
        int stagesCompleted,
        CancellationToken cancellationToken)
    {
        (int globalAfter, bool isComplete, Pcg32State rngAfter) =
            await LoadSeasonResultAsync(saveId, seasonNumber, cancellationToken).ConfigureAwait(false);

        if (!isComplete)
        {
            throw new InvalidOperationException(
                $"Season {seasonNumber} stopped at global stage {globalAfter} after {stagesCompleted} fast stages; expected a complete season.");
        }

        CompleteSeasonProgress progress = new(
            stagesCompleted,
            totalStages,
            globalBefore,
            globalAfter);

        return new CompleteSeasonResponse(
            saveId,
            seasonNumber,
            stagesCompleted,
            globalBefore,
            globalAfter,
            true,
            rngBefore.State,
            rngBefore.Stream,
            rngAfter.State,
            rngAfter.Stream,
            progress);
    }

    internal async Task<(int SeasonNumber, int GlobalStage, Pcg32State RngBefore, int TotalStages)> LoadSeasonCursorAsync(
        Guid saveId,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity season = await AdvanceRoundHandler.LoadSeasonAsync(context, metadata, cancellationToken).ConfigureAwait(false);
        GlobalStageGate.GlobalStageView global = await GlobalStageGate
            .LoadGlobalStageAsync(context, season, rules, cancellationToken)
            .ConfigureAwait(false);
        RngStateEntity rngRow = await AdvanceRoundHandler.LoadRngAsync(context, cancellationToken).ConfigureAwait(false);
        Pcg32State rngBefore = rngRow.ToState();

        if (global.IsSeasonComplete)
        {
            throw new CompleteSeasonConflictException(
                $"Season {season.SeasonNumber} is already complete; season completion is handled by the postseason chain.");
        }

        return (season.SeasonNumber, global.CurrentStage, rngBefore, rules.StagesPerSeason);
    }

    internal async Task<(int GlobalAfter, bool IsComplete, Pcg32State RngAfter)> LoadSeasonResultAsync(
        Guid saveId,
        int seasonNumber,
        CancellationToken cancellationToken)
    {
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await AdvanceRoundHandler.LoadMetadataAsync(context, saveId, cancellationToken).ConfigureAwait(false);
        RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false);
        SeasonEntity season = await context.Seasons
            .SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Save has no season {seasonNumber}.");
        _ = metadata;
        GlobalStageGate.GlobalStageView global = await GlobalStageGate
            .LoadGlobalStageAsync(context, season, rules, cancellationToken)
            .ConfigureAwait(false);
        RngStateEntity rngRow = await context.RngStates
            .AsNoTracking()
            .SingleAsync(e => e.Id == 1, cancellationToken)
            .ConfigureAwait(false);
        return (global.CurrentStage, global.IsSeasonComplete, rngRow.ToState());
    }
}
