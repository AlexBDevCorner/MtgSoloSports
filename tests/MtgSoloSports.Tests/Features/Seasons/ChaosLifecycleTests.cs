using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Cups.CompleteChaosCup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Seasons;

/// <summary>
/// MSS-106: Chaos runs between completed league tables and promotion qualifiers,
/// only for seasons started after activation. New saves follow the new chain;
/// pre-Chaos seasons keep the previous chain. Retries cannot skip or duplicate Chaos.
/// </summary>
public sealed class ChaosLifecycleTests
{
    [Fact]
    public async Task EligibleSeason1_SelectThenRun_GatesMovement()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos106-");
        try
        {
            GetSeasonStatusHandler status = new(store);
            GetSeasonStatusResponse complete = await status.HandleAsync(saveId);
            complete.LegalNextActions.ShouldBe([SeasonLifecycleActions.SelectChaosCup]);
            complete.ChaosEligible.ShouldBeTrue();
            complete.ChaosSelectionResolved.ShouldBeFalse();
            complete.ChaosComplete.ShouldBeFalse();
            complete.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.SeasonComplete));

            AdvanceToNextEventHandler advance = new(store);
            AdvanceToNextEventResponse selected = await advance.HandleAsync(saveId);
            selected.ExecutedAction.ShouldBe(SeasonLifecycleActions.SelectChaosCup);

            GetSeasonStatusResponse afterSelect = await status.HandleAsync(saveId);
            afterSelect.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunChaosCup]);
            afterSelect.ChaosSelectionResolved.ShouldBeTrue();
            afterSelect.ChaosComplete.ShouldBeFalse();
            afterSelect.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.ChaosSelectionResolved));

            // Incomplete Chaos blocks inaugural movement.
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await Should.ThrowAsync<CreateInauguralSuperleagueConflictException>(
                () => inaugural.HandleAsync(saveId));

            AdvanceToNextEventResponse ran = await advance.HandleAsync(saveId);
            ran.ExecutedAction.ShouldBe(SeasonLifecycleActions.RunChaosCup);

            GetSeasonStatusResponse afterRun = await status.HandleAsync(saveId);
            afterRun.LegalNextActions.ShouldBe([SeasonLifecycleActions.ResolveInauguralMovement]);
            afterRun.ChaosComplete.ShouldBeTrue();
            afterRun.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.ChaosComplete));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SelectChaos_Retry_IsIdempotent_WithoutDuplicateRng()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos106-retry-");
        try
        {
            SelectChaosCupFieldHandler selector = new(store);
            SelectChaosCupFieldResponse first = await selector.HandleAsync(saveId);
            (long stateAfterFirst, long streamAfterFirst) = await LoadRngAsync(store, saveId);

            SelectChaosCupFieldResponse second = await selector.HandleAsync(saveId, sourceSeasonNumber: 1);
            second.EditionChecksum.ShouldBe(first.EditionChecksum);
            second.Groups.Count.ShouldBe(first.Groups.Count);

            (long stateAfterSecond, long streamAfterSecond) = await LoadRngAsync(store, saveId);
            stateAfterSecond.ShouldBe(stateAfterFirst);
            streamAfterSecond.ShouldBe(streamAfterFirst);

            // Second tournament completion is also idempotent.
            CompleteChaosCupHandler completer = new(store);
            CompleteChaosCupResponse firstRun = await completer.CompleteCupAsync(saveId);
            firstRun.IsComplete.ShouldBeTrue();
            CompleteChaosCupResponse secondRun = await completer.CompleteCupAsync(saveId);
            secondRun.IsComplete.ShouldBeTrue();
            secondRun.RoundsPlayedTotal.ShouldBe(firstRun.RoundsPlayedTotal);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task IneligibleSeason_FollowsPreviousChain_WithoutChaos()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos106-legacy-");
        try
        {
            await SharedSaveTemplates.DisableChaosAsync(store, saveId);

            GetSeasonStatusHandler status = new(store);
            GetSeasonStatusResponse complete = await status.HandleAsync(saveId);
            complete.LegalNextActions.ShouldBe([SeasonLifecycleActions.ResolveInauguralMovement]);
            complete.ChaosEligible.ShouldBeFalse();

            SelectChaosCupFieldHandler selector = new(store);
            await Should.ThrowAsync<SelectChaosCupFieldConflictException>(
                () => selector.HandleAsync(saveId));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LegacyBackfill_WithPendingSuccessor_KeepsSuccessorEligible()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos106-backfill-");
        try
        {
            // Simulate old-chain postseason progress to a pending successor before upgrade.
            await SharedSaveTemplates.DisableChaosAsync(store, saveId);
            await new CreateInauguralSuperleagueHandler(store).HandleAsync(saveId);

            int current;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                SaveMetadataEntity metadata = await context.SaveMetadata.AsNoTracking().SingleAsync(e => e.Id == 1);
                current = metadata.CurrentSeason;
                current.ShouldBe(1);
                (await context.Seasons.MaxAsync(e => e.SeasonNumber)).ShouldBe(2);

                // Simulate a pre-MSS-106 save missing the activation row.
                List<ChaosCupActivationEntity> rows = await context.ChaosCupActivations.ToListAsync();
                context.ChaosCupActivations.RemoveRange(rows);
                await context.SaveChangesAsync();
            }

            store.InvalidateMigrationVerified(saveId);

            ChaosCupActivationEntity activation;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                activation = await ChaosCupLifecycle.EnsureActivationAsync(context, CancellationToken.None);
            }

            // First unstarted season is CurrentSeason + 1, not max persisted + 1.
            activation.EffectiveFromSeasonNumber.ShouldBe(current + 1);
            ChaosCupLifecycle.IsEligible(activation, current).ShouldBeFalse();
            ChaosCupLifecycle.IsEligible(activation, current + 1).ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ConcurrentFirstBackfills_BothSucceedWithOnePersistedRow()
    {
        // MSS-128: two first readers with an empty activation table must both observe
        // one validated row instead of one failing on the Id=1 unique constraint.
        // Incidental Season 1 history is forked; only the activation backfill races.
        // Deterministic: the loser is gated inside SaveChanges after observing the
        // missing row, the winner backfills and commits meanwhile, then the released
        // loser must recover via the persisted row (no lock contention involved).
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos128-race-");
        try
        {
            await RemoveActivationRowAsync(store, saveId);
            int current = await LoadCurrentSeasonAsync(store, saveId);

            var atGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using SaveDbContext loserContext = CreateGatedLoserContext(store, saveId, atGate, releaseGate);
            Task<ChaosCupActivationEntity> loserTask =
                ChaosCupLifecycle.EnsureActivationAsync(loserContext, CancellationToken.None);

            ChaosCupActivationEntity winner;
            try
            {
                (await atGate.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
                using SaveDbContext winnerContext = store.OpenDbContext(saveId);
                winner = await ChaosCupLifecycle.EnsureActivationAsync(winnerContext, CancellationToken.None);
            }
            finally
            {
                releaseGate.TrySetResult(true);
            }

            ChaosCupActivationEntity loser = await loserTask;
            winner.EffectiveFromSeasonNumber.ShouldBe(current + 1);
            loser.EffectiveFromSeasonNumber.ShouldBe(current + 1);
            loser.ChaosFormatVersion.ShouldBe(winner.ChaosFormatVersion);
            await AssertSingleActivationAsync(store, saveId, current + 1);

            // The loser must not keep a failed Added entity that could break later saves.
            loserContext.ChangeTracker.Entries<ChaosCupActivationEntity>().All(e => e.State != EntityState.Added).ShouldBeTrue();
            await loserContext.SaveChangesAsync();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task BackfillRetry_AfterUniqueFailure_ReturnsPersistedRow()
    {
        // Ordinary retry on a stale context: a duplicate insert fails, then the same
        // context still resolves to the persisted winner without keeping it tracked.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos128-retry-");
        try
        {
            await RemoveActivationRowAsync(store, saveId);

            ChaosCupActivationEntity winner;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                winner = await ChaosCupLifecycle.EnsureActivationAsync(context, CancellationToken.None);
            }

            using SaveDbContext stale = store.OpenDbContext(saveId);
            stale.ChaosCupActivations.Add(new ChaosCupActivationEntity
            {
                Id = ChaosCupLifecycle.ActivationId,
                EffectiveFromSeasonNumber = winner.EffectiveFromSeasonNumber + 1,
                ChaosFormatVersion = winner.ChaosFormatVersion,
            });
            await Should.ThrowAsync<DbUpdateException>(() => stale.SaveChangesAsync());

            ChaosCupActivationEntity retried = await ChaosCupLifecycle.EnsureActivationAsync(stale, CancellationToken.None);
            retried.EffectiveFromSeasonNumber.ShouldBe(winner.EffectiveFromSeasonNumber);
            retried.ChaosFormatVersion.ShouldBe(winner.ChaosFormatVersion);

            stale.ChangeTracker.Entries<ChaosCupActivationEntity>().All(e => e.State != EntityState.Added).ShouldBeTrue();
            await stale.SaveChangesAsync();

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                (await context.ChaosCupActivations.CountAsync()).ShouldBe(1);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task RemoveActivationRowAsync(Persistence.Saves.SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<ChaosCupActivationEntity> rows = await context.ChaosCupActivations.ToListAsync().ConfigureAwait(false);
        context.ChaosCupActivations.RemoveRange(rows);
        await context.SaveChangesAsync().ConfigureAwait(false);
        store.InvalidateMigrationVerified(saveId);
    }

    private static async Task<int> LoadCurrentSeasonAsync(Persistence.Saves.SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await context.SaveMetadata.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return metadata.CurrentSeason;
    }

    private static async Task AssertSingleActivationAsync(Persistence.Saves.SaveStore store, Guid saveId, int expectedEffectiveFrom)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<ChaosCupActivationEntity> persisted = await context.ChaosCupActivations.AsNoTracking().ToListAsync().ConfigureAwait(false);
        persisted.Count.ShouldBe(1);
        persisted[0].EffectiveFromSeasonNumber.ShouldBe(expectedEffectiveFrom);
    }

    private static SaveDbContext CreateGatedLoserContext(
        Persistence.Saves.SaveStore store,
        Guid saveId,
        TaskCompletionSource<bool> atGate,
        TaskCompletionSource<bool> releaseGate)
    {
        // Same save file and SQLite behavior as production contexts, plus a gate inside
        // SaveChanges: the loser signals after observing the missing row and waits until
        // the winner has backfilled, so the released insert deterministically collides.
        string path = store.GetSaveFilePath(saveId);
        DbContextOptions<SaveDbContext> options = new DbContextOptionsBuilder<SaveDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(new SaveSqliteConnectionInterceptor(), new GateSavingChangesInterceptor(atGate, releaseGate))
            .Options;
        return new SaveDbContext(options);
    }

    private sealed class GateSavingChangesInterceptor(
        TaskCompletionSource<bool> atGate,
        TaskCompletionSource<bool> releaseGate) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            atGate.TrySetResult(true);
            await releaseGate.Task.ConfigureAwait(false);
            return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<(long State, long Stream)> LoadRngAsync(Persistence.Saves.SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }
}
