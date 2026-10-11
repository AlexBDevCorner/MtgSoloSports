using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Qualifiers;

/// <summary>
/// MSS-075: full qualifier completion converges Dashboard and Qualifiers and
/// advances to feeder rebalancing without reruns.
/// </summary>
public sealed class Mss075FullCompletionTests
{
    [Fact]
    public async Task FullCompletion_Agrees_AndAdvancesToRebalance()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-mss075-full-");
        try
        {
            await PostseasonTestSaves.DrainToActionAsync(store, saveId, "RunQualifier");
            GetSeasonStatusResponse before = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            AssertBeforeState(before);

            RunAllQualifiersResponse runAll = await new RunAllQualifiersHandler(store).HandleAsync(saveId);
            AssertRunAllState(runAll, before);

            await AssertAfterStateAsync(store, saveId, runAll);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    private static void AssertBeforeState(GetSeasonStatusResponse before)
    {
        before.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunQualifier]);
        before.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.AutomaticMovementResolved));
        before.EventProgress.ShouldNotBeNull();
        before.EventProgress!.TotalRounds.ShouldBe(272);
        before.EventProgress.RoundsPlayed.ShouldBe(0);
        before.EventProgress.TotalEvents.ShouldBe((int?)17);
        before.EventProgress.CompletedEvents.ShouldBe((int?)0);
    }

    private static void AssertRunAllState(RunAllQualifiersResponse runAll, GetSeasonStatusResponse before)
    {
        runAll.FromSeasonNumber.ShouldBe(before.SourceSeasonNumber!.Value);
        runAll.ExecutedNow.Count.ShouldBe(17);
        runAll.AlreadyCompleted.Count.ShouldBe(0);
        runAll.TotalStandings.ShouldBe(288);
        runAll.TotalRounds.ShouldBe(272);
    }

    private static async Task AssertAfterStateAsync(SaveStore store, Guid saveId, RunAllQualifiersResponse runAll)
    {
        GetSeasonStatusResponse after = await new GetSeasonStatusHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        after.QualifierResolved.ShouldBeTrue();
        after.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.QualifierResolved));
        after.LegalNextActions.ShouldBe([SeasonLifecycleActions.RebalanceFeeders]);
        after.EventProgress.ShouldBeNull();
        // MSS-075 correction: completed totals remain available via
        // QualifierProgress even though the next legal action is rebalance.
        after.QualifierProgress.ShouldNotBeNull();
        after.QualifierProgress!.TotalEvents.ShouldBe(17);
        after.QualifierProgress.CompletedEvents.ShouldBe(17);
        after.QualifierProgress.TotalRounds.ShouldBe(272);
        after.QualifierProgress.RoundsPlayed.ShouldBe(272);

        GetQualifierListResponse list = await new GetQualifierListHandler(store).HandleAsync(
            saveId, fromSeasonNumber: runAll.FromSeasonNumber).ConfigureAwait(false);
        AssertListComplete(list);

        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            SaveMetadataEntity metadata = await context.SaveMetadata.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
            metadata.Phase.ShouldBe(SavePhaseParser.ToText(SavePhase.QualifierResolved));
        }

        RebalanceFeedersResponse rebalance = await new RebalanceFeedersHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        rebalance.FromSeasonNumber.ShouldBe(runAll.FromSeasonNumber);

        GetSeasonStatusResponse afterRebalance = await new GetSeasonStatusHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        afterRebalance.Rebalanced.ShouldBeTrue();
        // Completed qualifier totals survive rebalance for the done step.
        afterRebalance.QualifierProgress.ShouldNotBeNull();
        afterRebalance.QualifierProgress!.CompletedEvents.ShouldBe(17);
        afterRebalance.QualifierProgress.TotalEvents.ShouldBe(17);
    }

    private static void AssertListComplete(GetQualifierListResponse list)
    {
        list.Events.Count.ShouldBe(17);
        foreach (GetQualifierEventResponse evt in list.Events)
        {
            evt.RoundCount.ShouldBe(16);
            evt.Winners.ShouldBe(8);
            if (string.Equals(evt.Boundary, nameof(QualifierBoundary.Superleague), StringComparison.Ordinal))
            {
                evt.QualifierSize.ShouldBe(32);
            }
            else
            {
                evt.QualifierSize.ShouldBe(16);
            }
        }
    }
}
