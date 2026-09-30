using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Superleague.PlayQualifierRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Seasons;

public sealed class EventProgressTests
{
    [Fact]
    public async Task Status_BeforeQualifierRoundOne_ReportsZeroProgress()
    {
        var (store, root, saveId) = await PrepareLifecycleAsync(3131UL, 4141UL, "RunQualifier");
        try
        {
            GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            status.LegalNextActions.ShouldBe(["RunQualifier"]);
            status.EventProgress.ShouldNotBeNull();
            status.EventProgress!.Event.ShouldBe("qualifier");
            status.EventProgress.SourceSeasonNumber.ShouldBe(status.SourceSeasonNumber!.Value);
            status.EventProgress.RoundsPlayed.ShouldBe(0);
            status.EventProgress.TotalRounds.ShouldBe(16);
            status.EventProgress.GroupCount.ShouldBe(1);
            status.EventProgress.RoundsPerGroup.ShouldBe(16);
            status.EventProgress.Group.ShouldBeNull();
            status.EventProgress.RoundInGroup.ShouldBeNull();
            string.Equals(status.ComputedPhase, "QualifierInProgress", StringComparison.Ordinal).ShouldBeFalse();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Status_MidQualifier_ReportsInProgressPhaseAndRounds()
    {
        var (store, root, saveId) = await PrepareLifecycleAsync(3232UL, 4242UL, "RunQualifier");
        try
        {
            PlayQualifierRoundHandler step = new(store);
            await step.HandleAsync(saveId);
            await step.HandleAsync(saveId);
            await step.HandleAsync(saveId);
            GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            status.ComputedPhase.ShouldBe("QualifierInProgress");
            status.LegalNextActions.ShouldBe(["RunQualifier"]);
            status.EventProgress!.RoundsPlayed.ShouldBe(3);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AdvanceNextEvent_FinishesPartlyPlayedQualifier()
    {
        var (store, root, saveId) = await PrepareLifecycleAsync(3333UL, 4343UL, "RunQualifier");
        try
        {
            await new PlayQualifierRoundHandler(store).HandleAsync(saveId);
            AdvanceToNextEventResponse advanced = await new AdvanceToNextEventHandler(store).HandleAsync(saveId);
            advanced.ExecutedAction.ShouldBe("RunQualifier");
            advanced.QualifierResolved.ShouldBeTrue();
            advanced.EventProgress.ShouldBeNull();
            using SaveDbContext context = store.OpenDbContext(saveId);
            (await context.QualifierRounds.CountAsync()).ShouldBe(16);
            (await context.QualifierStandings.CountAsync()).ShouldBe(32);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Status_DuringLeaguePlay_HasNoEventProgress()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Progress League", 1UL, 2UL, UniverseTestCatalog.Build());
            GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(created.Detail.SaveId);
            status.EventProgress.ShouldBeNull();
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
