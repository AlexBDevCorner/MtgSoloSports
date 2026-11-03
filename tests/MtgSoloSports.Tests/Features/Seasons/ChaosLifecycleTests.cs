using Microsoft.EntityFrameworkCore;
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

    private static async Task<(long State, long Stream)> LoadRngAsync(Persistence.Saves.SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }
}
