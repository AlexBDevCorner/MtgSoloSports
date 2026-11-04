using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Cups.CompleteChaosCup;
using MtgSoloSports.Features.Cups.GetChaosCupAthleteJourney;
using MtgSoloSports.Features.Cups.GetChaosCupGroup;
using MtgSoloSports.Features.Cups.GetChaosCupSurvival;
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
/// MSS-108: athlete Chaos journeys and color/division survival reads.
/// Incidental Season 1 history is forked; the Chaos field selection, group
/// rounds and every read below still execute. The full-tournament finalist
/// case simulates the whole Cup because the six-stage path IS the subject.
/// </summary>
public sealed class ChaosCupJourneySurvivalTests
{
    [Fact]
    public async Task Journey_P16AndP17_MatchOfficialCutoff_WithoutGlobalRanks()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-journey-cutoff-");
        try
        {
            RunChaosCupGroupResponse simulated = await new RunChaosCupGroupHandler(store)
                .FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
            var p16 = simulated.Standings.Single(s => s.GroupRank == 16);
            var p17 = simulated.Standings.Single(s => s.GroupRank == 17);
            GetChaosCupGroupResponse group = await new GetChaosCupGroupHandler(store)
                .HandleGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);

            GetChaosCupAthleteJourneyHandler journeys = new(store);
            GetChaosCupAthleteJourneyResponse advanced = await journeys.HandleAsync(saveId, p16.SaveAthleteId, sourceSeasonNumber: 1);
            GetChaosCupAthleteJourneyResponse eliminated = await journeys.HandleAsync(saveId, p17.SaveAthleteId, sourceSeasonNumber: 1);

            advanced.SaveId.ShouldBe(saveId);
            advanced.SourceSeasonNumber.ShouldBe(1);
            advanced.AthleteId.ShouldBe(p16.SaveAthleteId);
            advanced.Stages.Count.ShouldBe(1);
            var advancedStage = advanced.Stages.Single(s => s.StageIndex == 0);
            advancedStage.GroupNumber.ShouldBe(1);
            advancedStage.IsComplete.ShouldBeTrue();
            advancedStage.IsOfficial.ShouldBeTrue();
            advancedStage.StandingsProvisional.ShouldBeFalse();
            advancedStage.GroupRank.ShouldBe(16);
            advancedStage.GroupScoreThousandths.ShouldBe(p16.GroupScoreThousandths);
            advancedStage.IsAdvanced.ShouldBe(true);
            advanced.IsEliminated.ShouldBeFalse();
            advanced.IsFinalist.ShouldBeFalse();
            advanced.ExitStage.ShouldBeNull();
            advanced.FinalPlace.ShouldBeNull();

            var eliminatedStage = eliminated.Stages.Single(s => s.StageIndex == 0);
            eliminatedStage.GroupRank.ShouldBe(17);
            eliminatedStage.GroupScoreThousandths.ShouldBe(p17.GroupScoreThousandths);
            eliminatedStage.IsAdvanced.ShouldBe(false);
            eliminated.IsEliminated.ShouldBeTrue();
            eliminated.EliminatedStageIndex.ShouldBe(0);
            eliminated.ExitStage.ShouldBe(ChaosCupFormat.ChaosExitStage.EliminatedInStageWith32Groups.ToString());
            eliminated.FinalPlace.ShouldBeNull();

            group.Cutoff.ShouldNotBeNull();
            group.Cutoff!.P16.SaveAthleteId.ShouldBe(p16.SaveAthleteId);
            group.Cutoff.P17.SaveAthleteId.ShouldBe(p17.SaveAthleteId);
            group.Cutoff.P16.SaveAthleteId.ShouldBe(advanced.AthleteId);
            group.Cutoff.P17.SaveAthleteId.ShouldBe(eliminated.AthleteId);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Journey_PoolAthlete_ProvisionalThenOfficial_MatchingPersistedStandings()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-journey-pool-");
        try
        {
            int poolAthlete = await LoadPoolAthleteAsync(store, saveId);
            int poolGroup = await LoadAthleteGroupAsync(store, saveId, stageIndex: 0, poolAthlete);
            GetChaosCupAthleteJourneyHandler journeys = new(store);

            GetChaosCupAthleteJourneyResponse provisional = await journeys.HandleAsync(saveId, poolAthlete, sourceSeasonNumber: 1);
            provisional.IsPoolInvite.ShouldBeTrue();
            provisional.SourceLabel.ShouldBe("Pool");
            provisional.SourceLeagueId.ShouldBeNull();
            var provisionalStage = provisional.Stages.Single(s => s.StageIndex == 0);
            provisionalStage.GroupNumber.ShouldBe(poolGroup);
            provisionalStage.IsComplete.ShouldBeFalse();
            provisionalStage.StandingsProvisional.ShouldBeTrue();
            provisionalStage.IsOfficial.ShouldBeFalse();
            provisionalStage.GroupRank.ShouldBeNull();
            provisionalStage.GroupScoreThousandths.ShouldBeNull();
            provisionalStage.IsAdvanced.ShouldBeNull();

            RunChaosCupGroupResponse simulated = await new RunChaosCupGroupHandler(store)
                .FinishGroupAsync(saveId, stageIndex: 0, groupNumber: poolGroup, sourceSeasonNumber: 1);
            var expected = simulated.Standings.Single(s => s.SaveAthleteId == poolAthlete);
            GetChaosCupAthleteJourneyResponse official = await journeys.HandleAsync(saveId, poolAthlete, sourceSeasonNumber: 1);
            var officialStage = official.Stages.Single(s => s.StageIndex == 0);
            officialStage.IsComplete.ShouldBeTrue();
            officialStage.IsOfficial.ShouldBeTrue();
            officialStage.GroupRank.ShouldBe(expected.GroupRank);
            officialStage.GroupScoreThousandths.ShouldBe(expected.GroupScoreThousandths);
            officialStage.BaseScoreThousandths.ShouldBe(expected.BaseScoreThousandths);
            officialStage.RoundWins.ShouldBe(expected.RoundWins);
            officialStage.IsAdvanced.ShouldBe(expected.IsAdvanced);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Journey_Finalist_PreservesSixStages_And_SurvivalSumsExactly()
    {
        // From-scratch proof for the six-stage path: completing the tournament IS the subject.
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-journey-final-");
        try
        {
            await new CompleteChaosCupHandler(store).CompleteCupAsync(saveId, sourceSeasonNumber: 1);
            int champion = await LoadFinalRankAthleteAsync(store, saveId, rank: 1);
            GetChaosCupAthleteJourneyResponse journey = await new GetChaosCupAthleteJourneyHandler(store)
                .HandleAsync(saveId, champion, sourceSeasonNumber: 1);
            AssertFinalistJourney(saveId, champion, journey);
            await AssertJourneyMatchesStandingsAsync(store, saveId, champion, journey);

            GetChaosCupSurvivalResponse survival = await new GetChaosCupSurvivalHandler(store)
                .HandleAsync(saveId, sourceSeasonNumber: 1);
            AssertCompleteSurvival(saveId, survival);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static void AssertFinalistJourney(Guid saveId, int champion, GetChaosCupAthleteJourneyResponse journey)
    {
        journey.SaveId.ShouldBe(saveId);
        journey.SourceSeasonNumber.ShouldBe(1);
        journey.AthleteId.ShouldBe(champion);
        journey.IsFinalist.ShouldBeTrue();
        journey.IsEliminated.ShouldBeFalse();
        journey.ExitStage.ShouldBe(nameof(ChaosCupFormat.ChaosExitStage.Finalist));
        journey.FinalPlace.ShouldBe(1);
        journey.Stages.Count.ShouldBe(6);
        journey.Stages.Select(s => s.StageIndex).ShouldBe([0, 1, 2, 3, 4, 5]);
        foreach (var stage in journey.Stages)
        {
            stage.IsComplete.ShouldBeTrue();
            stage.IsOfficial.ShouldBeTrue();
            stage.StandingsProvisional.ShouldBeFalse();
            stage.GroupRank.ShouldNotBeNull();
            stage.GroupScoreThousandths.ShouldNotBeNull();
        }

        journey.Stages.Single(s => s.StageIndex == 5).GroupRank.ShouldBe(1);
    }

    private static void AssertCompleteSurvival(Guid saveId, GetChaosCupSurvivalResponse survival)
    {
        survival.SaveId.ShouldBe(saveId);
        survival.SourceSeasonNumber.ShouldBe(1);
        survival.IsComplete.ShouldBeTrue();
        survival.Stages.Count.ShouldBe(6);
        int[] expectedInitial = [1024, 512, 256, 128, 64, 32];
        int[] expectedAdvanced = [512, 256, 128, 64, 32, 0];
        int[] expectedEliminated = [512, 256, 128, 64, 32, 0];
        int[] expectedFinalists = [0, 0, 0, 0, 0, 32];
        for (int stage = 0; stage < 6; stage++)
        {
            AssertCompleteStage(survival.Stages[stage], stage, expectedInitial[stage], expectedAdvanced[stage], expectedEliminated[stage], expectedFinalists[stage]);
        }
    }

    private static void AssertCompleteStage(GetChaosCupSurvivalResponse.StageSurvival summary, int stage, int initial, int advanced, int eliminated, int finalists)
    {
        summary.StageIndex.ShouldBe(stage);
        summary.IsDrawn.ShouldBeTrue();
        summary.IsComplete.ShouldBeTrue();
        summary.IsProvisional.ShouldBeFalse();
        summary.InitialCount.ShouldBe(initial);
        summary.AdvancedCount.ShouldBe(advanced);
        summary.EliminatedCount.ShouldBe(eliminated);
        summary.FinalistCount.ShouldBe(finalists);
        summary.PendingCount.ShouldBe(0);
        summary.ByColor.Count.ShouldBe(8);
        summary.ByDivision.Count.ShouldBe(5);
        summary.ByColor.Sum(b => b.Initial).ShouldBe(summary.InitialCount);
        summary.ByColor.Sum(b => b.Advanced).ShouldBe(summary.AdvancedCount);
        summary.ByColor.Sum(b => b.Eliminated).ShouldBe(summary.EliminatedCount);
        summary.ByColor.Sum(b => b.Pending).ShouldBe(summary.PendingCount);
        summary.ByColor.Sum(b => b.Finalists).ShouldBe(summary.FinalistCount);
        summary.ByDivision.Sum(b => b.Initial).ShouldBe(summary.InitialCount);
        summary.ByDivision.Sum(b => b.Advanced).ShouldBe(summary.AdvancedCount);
        summary.ByDivision.Sum(b => b.Eliminated).ShouldBe(summary.EliminatedCount);
        summary.ByDivision.Sum(b => b.Pending).ShouldBe(summary.PendingCount);
        summary.ByDivision.Sum(b => b.Finalists).ShouldBe(summary.FinalistCount);
    }

    [Fact]
    public async Task Survival_PartialStage_StaysProvisional_WithExactSums()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-survival-partial-");
        try
        {
            await new RunChaosCupGroupHandler(store)
                .FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
            GetChaosCupSurvivalResponse survival = await new GetChaosCupSurvivalHandler(store)
                .HandleAsync(saveId, sourceSeasonNumber: 1);

            var first = survival.Stages.Single(s => s.StageIndex == 0);
            first.IsDrawn.ShouldBeTrue();
            first.IsComplete.ShouldBeFalse();
            first.IsProvisional.ShouldBeTrue();
            first.InitialCount.ShouldBe(1024);
            first.AdvancedCount.ShouldBe(16);
            first.EliminatedCount.ShouldBe(16);
            first.PendingCount.ShouldBe(992);
            first.FinalistCount.ShouldBe(0);
            first.ByColor.Sum(b => b.Initial).ShouldBe(1024);
            first.ByColor.Sum(b => b.Advanced).ShouldBe(16);
            first.ByColor.Sum(b => b.Eliminated).ShouldBe(16);
            first.ByColor.Sum(b => b.Pending).ShouldBe(992);
            first.ByDivision.Sum(b => b.Initial).ShouldBe(1024);
            first.ByDivision.Sum(b => b.Advanced).ShouldBe(16);
            first.ByDivision.Sum(b => b.Eliminated).ShouldBe(16);
            first.ByDivision.Sum(b => b.Pending).ShouldBe(992);

            foreach (var later in survival.Stages.Skip(1))
            {
                later.IsDrawn.ShouldBeFalse();
                later.IsComplete.ShouldBeFalse();
                later.InitialCount.ShouldBe(0);
                later.AdvancedCount.ShouldBe(0);
                later.EliminatedCount.ShouldBe(0);
                later.PendingCount.ShouldBe(0);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Journey_And_Survival_AreSaveScoped_WithoutChaosLeakage()
    {
        var (storeA, rootA, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-journey-iso-a-");
        var (storeB, rootB, _) = await TestSaveStores.ForkAsync(storeA, saveId, "mtgsolosports-chaos-journey-iso-b-");
        try
        {
            await new SelectChaosCupFieldHandler(storeA).HandleAsync(saveId, sourceSeasonNumber: 1);
            await Should.ThrowAsync<GetChaosCupAthleteJourneyNotFoundException>(() =>
                new GetChaosCupAthleteJourneyHandler(storeB).HandleAsync(saveId, athleteId: 1, sourceSeasonNumber: 1));
            await Should.ThrowAsync<GetChaosCupSurvivalNotFoundException>(() =>
                new GetChaosCupSurvivalHandler(storeB).HandleAsync(saveId, sourceSeasonNumber: 1));

            int poolAthlete = await LoadPoolAthleteAsync(storeA, saveId);
            int poolGroup = await LoadAthleteGroupAsync(storeA, saveId, stageIndex: 0, poolAthlete);
            await new RunChaosCupGroupHandler(storeA)
                .FinishGroupAsync(saveId, stageIndex: 0, groupNumber: poolGroup, sourceSeasonNumber: 1);
            GetChaosCupAthleteJourneyResponse isolated = await new GetChaosCupAthleteJourneyHandler(storeA)
                .HandleAsync(saveId, poolAthlete, sourceSeasonNumber: 1);
            isolated.Stages.Single(s => s.StageIndex == 0).IsComplete.ShouldBeTrue();

            await new SelectChaosCupFieldHandler(storeB).HandleAsync(saveId, sourceSeasonNumber: 1);
            GetChaosCupAthleteJourneyResponse other = await new GetChaosCupAthleteJourneyHandler(storeB)
                .HandleAsync(saveId, poolAthlete, sourceSeasonNumber: 1);
            other.Stages.Single(s => s.StageIndex == 0).IsComplete.ShouldBeFalse();

            await Should.ThrowAsync<GetChaosCupAthleteJourneyNotFoundException>(() =>
                new GetChaosCupAthleteJourneyHandler(storeA).HandleAsync(saveId, athleteId: 999999, sourceSeasonNumber: 1));
        }
        finally
        {
            TestSaveStores.DeleteRoot(rootA);
            TestSaveStores.DeleteRoot(rootB);
        }
    }

    [Fact]
    public async Task Journey_And_Survival_Routes_ReturnOk_WithoutMutating()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-journey-http-");
        try
        {
            int poolAthlete = await LoadPoolAthleteAsync(store, saveId);
            int poolGroup = await LoadAthleteGroupAsync(store, saveId, stageIndex: 0, poolAthlete);
            await new RunChaosCupGroupHandler(store)
                .FinishGroupAsync(saveId, stageIndex: 0, groupNumber: poolGroup, sourceSeasonNumber: 1);

            string factoryRoot = Path.Combine(Path.GetTempPath(), "mtgsolosports-chaos-journey-api-" + Guid.NewGuid().ToString("N"));
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
                using HttpResponseMessage journey = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/athletes/{poolAthlete}?sourceSeason=1");
                journey.StatusCode.ShouldBe(HttpStatusCode.OK);
                using HttpResponseMessage survival = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/survival?sourceSeason=1");
                survival.StatusCode.ShouldBe(HttpStatusCode.OK);
                using HttpResponseMessage missingAthlete = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/athletes/999999?sourceSeason=1");
                missingAthlete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
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

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareFieldAsync(string prefix)
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync(prefix).ConfigureAwait(false);
        await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        return (store, root, saveId);
    }

    private static async Task<int> LoadPoolAthleteAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        ChaosCupEntrantEntity entrant = await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.IsPoolInvite)
            .OrderBy(e => e.SaveAthleteId)
            .FirstAsync()
            .ConfigureAwait(false);
        return entrant.SaveAthleteId;
    }

    private static async Task<int> LoadAthleteGroupAsync(SaveStore store, Guid saveId, int stageIndex, int athleteId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        ChaosCupDrawEntity draw = await context.ChaosCupDraws
            .AsNoTracking()
            .SingleAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.SaveAthleteId == athleteId)
            .ConfigureAwait(false);
        return draw.GroupNumber;
    }

    private static async Task<int> LoadFinalRankAthleteAsync(SaveStore store, Guid saveId, int rank)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        ChaosCupGroupStandingEntity standing = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .SingleAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == 5 && e.GroupRank == rank)
            .ConfigureAwait(false);
        return standing.SaveAthleteId;
    }

    private static async Task AssertJourneyMatchesStandingsAsync(
        SaveStore store,
        Guid saveId,
        int athleteId,
        GetChaosCupAthleteJourneyResponse journey)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> standings = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.SaveAthleteId == athleteId)
            .OrderBy(e => e.StageIndex)
            .ToListAsync()
            .ConfigureAwait(false);
        standings.Count.ShouldBe(6);
        standings.Select(s => s.StageIndex).ShouldBe([0, 1, 2, 3, 4, 5]);
        foreach (ChaosCupGroupStandingEntity standing in standings)
        {
            var entry = journey.Stages.Single(s => s.StageIndex == standing.StageIndex);
            entry.GroupNumber.ShouldBe(standing.GroupNumber);
            entry.GroupRank.ShouldBe(standing.GroupRank);
            entry.GroupScoreThousandths.ShouldBe(standing.GroupScoreThousandths);
            entry.BaseScoreThousandths.ShouldBe(standing.BaseScoreThousandths);
            entry.RoundWins.ShouldBe(standing.RoundWins);
        }
    }
}
