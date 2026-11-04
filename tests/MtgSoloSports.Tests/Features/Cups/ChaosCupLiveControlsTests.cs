using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Cups.AdvanceChaosCupStage;
using MtgSoloSports.Features.Cups.CompleteChaosCup;
using MtgSoloSports.Features.Cups.RunChaosCupGroup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-110: scoped Chaos Live controls. Proves the mapped HTTP routes match the
/// advertised scopes (one game round, one group, current stage, explicit
/// next-stage draw, full Cup), scoped-vs-fast-forward results agree, draw and
/// reload are safe, and Dashboard status agrees with Live playability including
/// interrupted work.
/// </summary>
public sealed class ChaosCupLiveControlsTests
{
    [Fact]
    public async Task ScopedHttpRoutes_MatchAdvertisedActions_AndStayReplayable()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-live-routes-");
        string factoryRoot = Path.Combine(Path.GetTempPath(), "mtgsolosports-chaos-live-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(factoryRoot);
        string savesRoot = Path.Combine(factoryRoot, "saves");
        Directory.CreateDirectory(savesRoot);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = savesRoot);
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(factoryRoot, "catalog.db"));
            }));
        try
        {
            string source = store.GetSaveFilePath(saveId);
            string destination = SaveFileNaming.GetSaveFilePath(savesRoot, saveId);
            await TestSaveStores.CopySaveFileLockedAsync(store, saveId, source, destination);
            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage playNext = await client.PostAsync(
                $"/api/saves/{saveId:D}/cups/chaos/stages/0/groups/1/rounds?sourceSeason=1", null);
            playNext.StatusCode.ShouldBe(HttpStatusCode.OK);

            using HttpResponseMessage finishGroup = await client.PostAsync(
                $"/api/saves/{saveId:D}/cups/chaos/stages/0/groups/1/complete?sourceSeason=1", null);
            finishGroup.StatusCode.ShouldBe(HttpStatusCode.OK);

            using HttpResponseMessage replayGroup = await client.GetAsync(
                $"/api/saves/{saveId:D}/cups/chaos/stages/0/groups/1?sourceSeason=1");
            replayGroup.StatusCode.ShouldBe(HttpStatusCode.OK);

            using HttpResponseMessage replayRound = await client.GetAsync(
                $"/api/saves/{saveId:D}/cups/chaos/stages/0/groups/1/rounds/1?sourceSeason=1");
            replayRound.StatusCode.ShouldBe(HttpStatusCode.OK);

            using HttpResponseMessage extraRound = await client.PostAsync(
                $"/api/saves/{saveId:D}/cups/chaos/stages/0/groups/1/rounds?sourceSeason=1", null);
            extraRound.StatusCode.ShouldBe(HttpStatusCode.Conflict);

            using HttpResponseMessage completeStage = await client.PostAsync(
                $"/api/saves/{saveId:D}/cups/chaos/stages/0/complete?sourceSeason=1", null);
            completeStage.StatusCode.ShouldBe(HttpStatusCode.OK);

            using HttpResponseMessage drawNext = await client.PostAsync(
                $"/api/saves/{saveId:D}/cups/chaos/stages/1/draw?sourceSeason=1", null);
            drawNext.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(factoryRoot);
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ScopedScopes_AgreeWithFastForward_AndOrderIndependent()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-live-agree-");
        try
        {
            var (storeA, rootA, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-live-manual-");
            var (storeB, rootB, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-live-bulk-");
            try
            {
                RunChaosCupGroupHandler manual = new(storeA);
                await manual.PlayNextAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
                await manual.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
                CompleteChaosCupStageResponse staged =
                    await new CompleteChaosCupHandler(storeA).CompleteStageAsync(saveId, stageIndex: 0, sourceSeasonNumber: 1);
                staged.IsComplete.ShouldBeTrue();
                await new AdvanceChaosCupStageHandler(storeA).HandleAsync(saveId, targetStageIndex: 1, sourceSeasonNumber: 1);
                CompleteChaosCupResponse cupped =
                    await new CompleteChaosCupHandler(storeA).CompleteCupAsync(saveId, sourceSeasonNumber: 1);
                cupped.IsComplete.ShouldBeTrue();

                CompleteChaosCupResponse fast =
                    await new CompleteChaosCupHandler(storeB).CompleteCupAsync(saveId, sourceSeasonNumber: 1);
                fast.IsComplete.ShouldBeTrue();
                fast.RoundsPlayedTotal.ShouldBe(cupped.RoundsPlayedTotal);

                await AssertTournamentsEqualAsync(storeA, storeB, saveId);
            }
            finally
            {
                TestSaveStores.DeleteRoot(rootA);
                TestSaveStores.DeleteRoot(rootB);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DrawNextStage_IsSafe_Idempotent_AndReloadable()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-live-draw-");
        try
        {
            await Should.ThrowAsync<AdvanceChaosCupStageConflictException>(() =>
                new AdvanceChaosCupStageHandler(store).HandleAsync(saveId, targetStageIndex: 1, sourceSeasonNumber: 1));

            RunChaosCupGroupHandler groups = new(store);
            for (int group = 1; group <= ChaosCupFormat.GroupCountForStage(0); group++)
            {
                await groups.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: group, sourceSeasonNumber: 1);
            }

            AdvanceChaosCupStageResponse first =
                await new AdvanceChaosCupStageHandler(store).HandleAsync(saveId, targetStageIndex: 1, sourceSeasonNumber: 1);
            first.StageIndex.ShouldBe(1);

            AdvanceChaosCupStageResponse retry =
                await new AdvanceChaosCupStageHandler(store).HandleAsync(saveId, targetStageIndex: 1, sourceSeasonNumber: 1);
            retry.DrawChecksum.ShouldBe(first.DrawChecksum);

            AdvanceChaosCupStageResponse reloaded =
                await new AdvanceChaosCupStageHandler(store).GetAsync(saveId, stageIndex: 1, sourceSeasonNumber: 1);
            reloaded.DrawChecksum.ShouldBe(first.DrawChecksum);
            reloaded.Groups.Count.ShouldBe(ChaosCupFormat.GroupCountForStage(1));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Status_AgreesWithLivePlayability_IncludingInterruptedWork()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-live-status-");
        try
        {
            GetSeasonStatusResponse before = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            before.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunChaosCup]);
            before.ChaosSelectionResolved.ShouldBeTrue();
            before.ChaosComplete.ShouldBeFalse();

            await new RunChaosCupGroupHandler(store).PlayNextAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
            GetSeasonStatusResponse interrupted = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            interrupted.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunChaosCup]);
            interrupted.ChaosComplete.ShouldBeFalse();

            await new CompleteChaosCupHandler(store).CompleteCupAsync(saveId, sourceSeasonNumber: 1);
            GetSeasonStatusResponse after = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            after.ChaosComplete.ShouldBeTrue();
            after.LegalNextActions.ShouldBe([SeasonLifecycleActions.ResolveInauguralMovement]);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareFieldAsync(string prefix)
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync(prefix).ConfigureAwait(false);
        await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        return (store, root, saveId);
    }

    private static async Task AssertTournamentsEqualAsync(SaveStore left, SaveStore right, Guid saveId)
    {
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            string leftDraw = await LoadStageDrawChecksumAsync(left, saveId, stage).ConfigureAwait(false);
            string rightDraw = await LoadStageDrawChecksumAsync(right, saveId, stage).ConfigureAwait(false);
            leftDraw.ShouldBe(rightDraw);
            int leftRounds = await CountStageRoundsAsync(left, saveId, stage).ConfigureAwait(false);
            int rightRounds = await CountStageRoundsAsync(right, saveId, stage).ConfigureAwait(false);
            leftRounds.ShouldBe(rightRounds);
        }
    }

    private static async Task<string> LoadStageDrawChecksumAsync(SaveStore store, Guid saveId, int stageIndex)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
            .ToListAsync().ConfigureAwait(false);
        draws.Count.ShouldBe(ChaosCupFormat.EntrantCountForStage(stageIndex));
        return draws[0].DrawChecksum;
    }

    private static async Task<int> CountStageRoundsAsync(SaveStore store, Guid saveId, int stageIndex)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        return await context.ChaosCupRounds
            .AsNoTracking()
            .CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex).ConfigureAwait(false);
    }
}
