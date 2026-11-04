using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Cups.GetChaosCupEdition;
using MtgSoloSports.Features.Cups.GetChaosCupGroup;
using MtgSoloSports.Features.Cups.RunChaosCupGroup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-107: read-only Chaos browsing. Proves the edition overview carries six
/// stage summaries without payloads, a group replays from its own 32 entrants
/// (never the 1024-athlete edition), the P16/P17 cutoff is provisional until
/// the 16th round and official after, and undrawn stages (including a
/// nonexistent Final) 404 instead of reading as complete. Incidental Season 1
/// history is forked; the Chaos field selection, group rounds and every read
/// below still execute.
/// </summary>
public sealed class ChaosCupReadApiTests
{
    [Fact]
    public async Task Edition_ReturnsSixStageSummaries_WithoutPayloads()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-read-edition-");
        try
        {
            GetChaosCupEditionResponse edition =
                await new GetChaosCupEditionHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            edition.SourceSeasonNumber.ShouldBe(1);
            edition.FieldSize.ShouldBe(ChaosCupFormat.FieldSize);
            edition.State.ShouldBe("InProgress");
            edition.IsComplete.ShouldBeFalse();
            edition.StageCount.ShouldBe(ChaosCupFormat.StageCount);
            edition.TotalGroups.ShouldBe(63);
            edition.TotalRounds.ShouldBe(1008);
            edition.RoundsPlayed.ShouldBe(0);
            edition.GroupsCompleted.ShouldBe(0);
            edition.Stages.Count.ShouldBe(6);
            edition.Stages.Select(s => s.GroupCount).ShouldBe([32, 16, 8, 4, 2, 1]);
            GetChaosCupEditionResponse.StageSummary first = edition.Stages[0];
            first.IsDrawn.ShouldBeTrue();
            first.IsComplete.ShouldBeFalse();
            first.State.ShouldBe("InProgress");
            first.DrawChecksum.ShouldNotBeNullOrWhiteSpace();
            foreach (GetChaosCupEditionResponse.StageSummary later in edition.Stages.Skip(1))
            {
                later.IsDrawn.ShouldBeFalse();
                later.IsComplete.ShouldBeFalse();
                later.State.ShouldBe(GetChaosCupEditionResponse.NotDrawn);
                later.DrawChecksum.ShouldBe(string.Empty);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Edition_WithoutField_ReturnsNotFound_KeepingPriorSavesCompatible()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-read-nofield-");
        try
        {
            await Should.ThrowAsync<GetChaosCupEditionNotFoundException>(() =>
                new GetChaosCupEditionHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1));
            await Should.ThrowAsync<GetChaosCupGroupNotFoundException>(() =>
                new GetChaosCupGroupHandler(store).HandleGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Group_NonfinalPartial_ReturnsProvisionalCutoff_AndGetNeverSimulates()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-read-partial-");
        try
        {
            (long rngBefore, long streamBefore) = await LoadGlobalRngAsync(store, saveId);
            await new RunChaosCupGroupHandler(store).PlayNextAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
            GetChaosCupGroupHandler reads = new(store);
            GetChaosCupGroupResponse group =
                await reads.HandleGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
            group.SourceSeasonNumber.ShouldBe(1);
            group.StageIndex.ShouldBe(0);
            group.GroupNumber.ShouldBe(1);
            group.IsFinal.ShouldBeFalse();
            group.State.ShouldBe("InProgress");
            group.RoundsPlayed.ShouldBe(1);
            group.TotalRounds.ShouldBe(ChaosCupFormat.RoundsPerGroup);
            group.IsComplete.ShouldBeFalse();
            group.StandingsProvisional.ShouldBeTrue();
            group.Entrants.Count.ShouldBe(ChaosCupFormat.GroupSize);
            group.Standings.Count.ShouldBe(ChaosCupFormat.GroupSize);
            group.Rounds.Count.ShouldBe(1);
            foreach (GetChaosCupGroupResponse.Entrant entrant in group.Entrants)
            {
                entrant.Name.ShouldNotBeNullOrWhiteSpace();
                entrant.SourceLabel.ShouldNotBeNullOrWhiteSpace();
                if (entrant.IsPoolInvite)
                {
                    entrant.SourceLabel.ShouldBe("Pool");
                    entrant.SourceLeagueId.ShouldBeNull();
                }
                else
                {
                    entrant.SourceLeagueId.ShouldNotBeNull();
                }
            }

            group.Cutoff.ShouldNotBeNull();
            group.Cutoff!.IsProvisional.ShouldBeTrue();
            group.Cutoff.IsOfficial.ShouldBeFalse();
            group.Cutoff.P16.GroupRank.ShouldBe(16);
            group.Cutoff.P17.GroupRank.ShouldBe(17);
            group.Cutoff.GapThousandths.ShouldBe(group.Cutoff.P16.GroupScoreThousandths - group.Cutoff.P17.GroupScoreThousandths);
            (long rngAfter, long streamAfter) = await LoadGlobalRngAsync(store, saveId);
            rngAfter.ShouldBe(rngBefore);
            streamAfter.ShouldBe(streamBefore);
            int roundsAfter = await CountGroupRoundsAsync(store, saveId, stageIndex: 0, groupNumber: 1);
            roundsAfter.ShouldBe(1);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Group_NonfinalComplete_ReturnsOfficialCutoff_MatchingSimulationStandings()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-read-complete-");
        try
        {
            RunChaosCupGroupResponse simulated =
                await new RunChaosCupGroupHandler(store).FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 2, sourceSeasonNumber: 1);
            GetChaosCupGroupResponse group =
                await new GetChaosCupGroupHandler(store).HandleGroupAsync(saveId, stageIndex: 0, groupNumber: 2, sourceSeasonNumber: 1);
            group.IsComplete.ShouldBeTrue();
            group.State.ShouldBe("Completed");
            group.StandingsProvisional.ShouldBeFalse();
            group.RoundsPlayed.ShouldBe(ChaosCupFormat.RoundsPerGroup);
            group.Rounds.Count.ShouldBe(ChaosCupFormat.RoundsPerGroup);
            group.Cutoff.ShouldNotBeNull();
            group.Cutoff!.IsOfficial.ShouldBeTrue();
            group.Cutoff.IsProvisional.ShouldBeFalse();
            group.Standings.Select(s => s.SaveAthleteId)
                .ShouldBe(simulated.Standings.Select(s => s.SaveAthleteId).ToList());
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Round_ReplaysSingleGroup_WithoutFullEdition_AndUnplayedRound404s()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-read-round-");
        try
        {
            await new RunChaosCupGroupHandler(store).PlayNextAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
            GetChaosCupGroupHandler reads = new(store);
            GetChaosCupGroupResponse group =
                await reads.HandleGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
            group.Entrants.Count.ShouldBe(ChaosCupFormat.GroupSize);
            GetChaosCupRoundResponse round =
                await reads.HandleRoundAsync(saveId, stageIndex: 0, groupNumber: 1, roundNumber: 1, sourceSeasonNumber: 1);
            round.Placements.Count.ShouldBe(ChaosCupFormat.GroupSize);
            round.Placements.Select(p => p.Position).OrderBy(p => p).ShouldBe(Enumerable.Range(1, ChaosCupFormat.GroupSize).ToList());
            round.Checksum.ShouldBe(group.Rounds.Single(r => r.RoundNumber == 1).Checksum);
            await Should.ThrowAsync<GetChaosCupGroupNotFoundException>(() =>
                reads.HandleRoundAsync(saveId, stageIndex: 0, groupNumber: 1, roundNumber: 2, sourceSeasonNumber: 1));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Final_NotDrawn_ReturnsNotFound_NeverCompleted()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-read-final-");
        try
        {
            GetChaosCupEditionResponse edition =
                await new GetChaosCupEditionHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            GetChaosCupEditionResponse.StageSummary final = edition.Stages.Single(s => s.IsFinal);
            final.StageIndex.ShouldBe(5);
            final.GroupCount.ShouldBe(1);
            final.IsDrawn.ShouldBeFalse();
            final.IsComplete.ShouldBeFalse();
            final.State.ShouldBe(GetChaosCupEditionResponse.NotDrawn);
            await Should.ThrowAsync<GetChaosCupGroupNotFoundException>(() =>
                new GetChaosCupGroupHandler(store).HandleGroupAsync(saveId, stageIndex: 5, groupNumber: 1, sourceSeasonNumber: 1));
            await Should.ThrowAsync<GetChaosCupGroupNotFoundException>(() =>
                new GetChaosCupGroupHandler(store).HandleRoundAsync(saveId, stageIndex: 5, groupNumber: 1, roundNumber: 1, sourceSeasonNumber: 1));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ChaosReadRoutes_ReturnOk_WithoutMutating()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-read-http-");
        try
        {
            await new RunChaosCupGroupHandler(store).PlayNextAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
            string factoryRoot = Path.Combine(Path.GetTempPath(), "mtgsolosports-chaos-read-api-" + Guid.NewGuid().ToString("N"));
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
                await AssertChaosReadRoutesAsync(client, saveId);
            }
            finally
            {
                factory.Dispose();
                TestSaveStores.DeleteRoot(factoryRoot);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task AssertChaosReadRoutesAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage edition = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos?sourceSeason=1").ConfigureAwait(false);
        edition.StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage group = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/stages/0/groups/1?sourceSeason=1").ConfigureAwait(false);
        group.StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage round = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/stages/0/groups/1/rounds/1?sourceSeason=1").ConfigureAwait(false);
        round.StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage missingRound = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/stages/0/groups/1/rounds/2?sourceSeason=1").ConfigureAwait(false);
        missingRound.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using HttpResponseMessage missingFinal = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/stages/5/groups/1?sourceSeason=1").ConfigureAwait(false);
        missingFinal.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareFieldAsync(string prefix)
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync(prefix).ConfigureAwait(false);
        await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        return (store, root, saveId);
    }

    private static async Task<(long State, long Stream)> LoadGlobalRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }

    private static async Task<int> CountGroupRoundsAsync(SaveStore store, Guid saveId, int stageIndex, int groupNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        return await context.ChaosCupRounds.CountAsync(
            e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.GroupNumber == groupNumber).ConfigureAwait(false);
    }
}
