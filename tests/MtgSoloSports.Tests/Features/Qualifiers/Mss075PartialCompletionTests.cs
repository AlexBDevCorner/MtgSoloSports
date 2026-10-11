using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Qualifiers;

/// <summary>
/// MSS-075: partial qualifier progress is accurately represented on both pages.
/// </summary>
public sealed class Mss075PartialCompletionTests
{
    [Fact]
    public async Task PartialCompletion_IsAccuratelyRepresented()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-mss075-partial-");
        try
        {
            await PostseasonTestSaves.DrainToActionAsync(store, saveId, "RunQualifier");
            GetSeasonStatusResponse before = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            int source = before.SourceSeasonNumber!.Value;

            await new RunQualifierHandler(store).HandleAsync(saveId);
            await AssertSuperOnlyAsync(store, saveId, source);

            await new BoundaryQualifierRunner(store).HandleAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White);
            await AssertSuperPlusOneFeederAsync(store, saveId, source);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    private static async Task AssertSuperOnlyAsync(SaveStore store, Guid saveId, int source)
    {
        GetSeasonStatusResponse afterSuper = await new GetSeasonStatusHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        afterSuper.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunQualifier]);
        afterSuper.QualifierResolved.ShouldBeFalse();
        afterSuper.EventProgress.ShouldNotBeNull();
        afterSuper.EventProgress!.RoundsPlayed.ShouldBe(16);
        afterSuper.EventProgress.TotalRounds.ShouldBe(272);
        afterSuper.EventProgress.CompletedEvents.ShouldBe((int?)1);
        afterSuper.EventProgress.TotalEvents.ShouldBe((int?)17);

        GetQualifierListResponse list = await new GetQualifierListHandler(store).HandleAsync(saveId, fromSeasonNumber: source).ConfigureAwait(false);
        list.Events.Count.ShouldBe(1);

        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            SaveMetadataEntity metadata = await context.SaveMetadata.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
            metadata.Phase.ShouldBe(SavePhaseParser.ToText(SavePhase.AutomaticMovementResolved));
        }
    }

    private static async Task AssertSuperPlusOneFeederAsync(SaveStore store, Guid saveId, int source)
    {
        GetSeasonStatusResponse afterOneFeeder = await new GetSeasonStatusHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        afterOneFeeder.EventProgress.ShouldNotBeNull();
        afterOneFeeder.EventProgress!.RoundsPlayed.ShouldBe(32);
        afterOneFeeder.EventProgress.CompletedEvents.ShouldBe((int?)2);

        GetQualifierListResponse list2 = await new GetQualifierListHandler(store).HandleAsync(saveId, fromSeasonNumber: source).ConfigureAwait(false);
        list2.Events.Count.ShouldBe(2);
    }
}
