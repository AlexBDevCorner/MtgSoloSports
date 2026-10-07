using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Leagues.CurrentStandings;
using MtgSoloSports.Features.Leagues.SeasonProgress;
using MtgSoloSports.Features.Leagues.SeasonTable;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Simulation;

public sealed class SeasonCompletionTests
{
    [Fact]
    public async Task StageSync_BlocksNextStageUntilAllLeaguesComplete()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Stage Sync", 111UL, 222UL, UniverseTestCatalog.Build());
            List<int> allLeagues = await AllLeagueIdsAsync(store, created.Detail.SaveId);
            allLeagues.Count.ShouldBe(24);
            int first = allLeagues[0];

            CompleteStageHandler completer = new(store);
            AdvanceRoundHandler advancer = new(store);
            GetSeasonProgressHandler progress = new(store);

            GetSeasonProgressResponse initial = await progress.HandleAsync(created.Detail.SaveId, 1);
            initial.GlobalStage.ShouldBe(1);
            initial.IsSeasonComplete.ShouldBeFalse();

            // First league completes Stage 1 alone: allowed within the global stage.
            CompleteStageResponse firstCompleted = await completer.HandleAsync(created.Detail.SaveId, first);
            firstCompleted.StageNumber.ShouldBe(1);

            // Same league cannot begin Stage 2 (round or stage) until Stage 1 is complete everywhere.
            await Should.ThrowAsync<CompleteStageConflictException>(() => completer.HandleAsync(created.Detail.SaveId, first));
            await Should.ThrowAsync<AdvanceRoundConflictException>(() => advancer.HandleAsync(created.Detail.SaveId, first));

            GetSeasonProgressResponse partial = await progress.HandleAsync(created.Detail.SaveId, 1);
            partial.GlobalStage.ShouldBe(1);
            partial.IsSeasonComplete.ShouldBeFalse();

            // Remaining leagues complete Stage 1 one by one.
            foreach (int other in allLeagues.Skip(1))
            {
                await completer.HandleAsync(created.Detail.SaveId, other);
            }

            GetSeasonProgressResponse synced = await progress.HandleAsync(created.Detail.SaveId, 1);
            synced.GlobalStage.ShouldBe(2);

            // Stage 2 is now legal for the first league.
            AdvanceRoundResponse next = await advancer.HandleAsync(created.Detail.SaveId, first);
            next.StageNumber.ShouldBe(2);
            next.RoundNumber.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteStageForAllLeagues_EquivalentToSequentialInCanonicalOrder()
    {
        // MSS-067: second save forked instead of creating the same universe twice.
        var (store, root) = CreateStore();
        try
        {
            IReadOnlyList<MtgSoloSports.Features.Catalog.ImportCatalog.CatalogAthlete> catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord viaSequential = await store.CreateAsync("Bulk Sequential", 9001UL, 7002UL, catalog);
            var (bulkStore, bulkRoot, viaBulkId) = await TestSaveStores.ForkAsync(store, viaSequential.Detail.SaveId, "mtgsolosports-bulk-");
            try
            {
                List<int> sequentialLeagues = await AllLeagueIdsAsync(store, viaSequential.Detail.SaveId);
                List<int> bulkLeagues = await AllLeagueIdsAsync(bulkStore, viaBulkId);
                sequentialLeagues.Count.ShouldBe(24);
                bulkLeagues.Count.ShouldBe(24);

                // Path A: 24 sequential single-league completions in canonical id order.
                CompleteStageHandler single = new(store);
                List<string> sequentialChecksums = new(24);
                foreach (int leagueId in sequentialLeagues)
                {
                    CompleteStageResponse completed = await single.HandleAsync(viaSequential.Detail.SaveId, leagueId);
                    completed.StageNumber.ShouldBe(1);
                    sequentialChecksums.Add(completed.StageChecksum);
                }

                // Path B: one bulk global-stage completion from the same start state.
                CompleteStageForAllLeaguesHandler bulk = new(bulkStore);
                CompleteStageForAllLeaguesResponse bulkResponse = await bulk.HandleAsync(viaBulkId);

                bulkResponse.CompletedStage.ShouldBe(1);
                bulkResponse.GlobalStageAfter.ShouldBe(2);
                bulkResponse.IsSeasonComplete.ShouldBeFalse();
                bulkResponse.Leagues.Count.ShouldBe(24);
                bulkResponse.Leagues.Select(l => l.StageChecksum).ShouldBe(sequentialChecksums);

                // RNG-after commits identically for equivalent operation order.
                ulong sequentialRng = await LoadRngStateAsync(store, viaSequential.Detail.SaveId);
                ulong bulkRng = await LoadRngStateAsync(bulkStore, viaBulkId);
                sequentialRng.ShouldBe(bulkRng);
            }
            finally
            {
                TestSaveStores.DeleteRoot(bulkRoot);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteStageForAllLeagues_PartialCompletesRemainingOnly()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Bulk Partial", 333UL, 444UL, UniverseTestCatalog.Build());
            List<int> allLeagues = await AllLeagueIdsAsync(store, created.Detail.SaveId);

            CompleteStageHandler single = new(store);
            foreach (int leagueId in allLeagues.Take(3))
            {
                await single.HandleAsync(created.Detail.SaveId, leagueId);
            }

            CompleteStageForAllLeaguesHandler bulk = new(store);
            CompleteStageForAllLeaguesResponse response = await bulk.HandleAsync(created.Detail.SaveId);

            response.CompletedStage.ShouldBe(1);
            response.Leagues.Count.ShouldBe(21);
            response.GlobalStageAfter.ShouldBe(2);

            GetSeasonProgressHandler progress = new(store);
            GetSeasonProgressResponse after = await progress.HandleAsync(created.Detail.SaveId, 1);
            after.GlobalStage.ShouldBe(2);
            after.Leagues.All(l => l.CompletedStages == 1).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentStandings_ProvisionalAccumulatesChampionship_Deterministic()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Current Standings", 555UL, 666UL, UniverseTestCatalog.Build());
            List<int> allLeagues = await AllLeagueIdsAsync(store, created.Detail.SaveId);
            int first = allLeagues[0];

            GetCurrentStandingsHandler standings = new(store);
            GetCurrentStandingsResponse empty = await standings.HandleAsync(created.Detail.SaveId, first);
            empty.CompletedStages.ShouldBe(0);
            empty.IsFinal.ShouldBeFalse();
            empty.Standings.Count.ShouldBe(0);

            CompleteStageForAllLeaguesHandler bulk = new(store);
            await bulk.HandleAsync(created.Detail.SaveId);

            GetCurrentStandingsResponse current = await standings.HandleAsync(created.Detail.SaveId, first);
            current.CompletedStages.ShouldBe(1);
            current.GlobalStage.ShouldBe(2);
            current.IsFinal.ShouldBeFalse();
            current.IsSeasonComplete.ShouldBeFalse();
            current.Standings.Count.ShouldBe(32);
            current.Standings.Select(s => s.SeasonRank).ShouldBe(Enumerable.Range(1, 32).ToList());
            current.SeasonChecksum.Length.ShouldBe(64);

            // Single-stage provisional totals equal the stage championship points.
            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            Dictionary<int, int> stageChampionship = await context.StageStandings
                .AsNoTracking()
                .Where(e => e.LeagueId == first && e.StageNumber == 1)
                .ToDictionaryAsync(e => e.SaveAthleteId, e => e.ChampionshipPointsThousandths);
            foreach (CurrentStandingEntry entry in current.Standings)
            {
                entry.TotalChampionshipPointsThousandths.ShouldBe(stageChampionship[entry.AthleteId]);
                entry.IsChampion.ShouldBeFalse();
            }

            // Deterministic: repeated reads agree.
            GetCurrentStandingsResponse again = await standings.HandleAsync(created.Detail.SaveId, first);
            again.SeasonChecksum.ShouldBe(current.SeasonChecksum);
            again.Standings.Select(s => s.AthleteId).ShouldBe(current.Standings.Select(s => s.AthleteId).ToList());

            // Season table does not exist yet.
            GetSeasonTableHandler table = new(store);
            await Should.ThrowAsync<SeasonTableNotFoundException>(() => table.HandleAsync(created.Detail.SaveId, first, 1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SeasonFinalization_AfterStage32_PersistsStandingsAndChampion()
    {
        // MSS-067: shared Season 1 template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-seasonfinal-");
        try
        {
            List<int> allLeagues = await AllLeagueIdsAsync(store, saveId);
            int first = allLeagues[0];

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            season.IsComplete.ShouldBeTrue();

            foreach (int leagueId in allLeagues)
            {
                await AssertFinalLeagueAsync(context, saveId, season.Id, leagueId);
            }

            // Current standings now read the persisted final table.
            GetCurrentStandingsHandler standings = new(store);
            GetCurrentStandingsResponse current = await standings.HandleAsync(saveId, first);
            current.IsFinal.ShouldBeTrue();
            current.IsSeasonComplete.ShouldBeTrue();
            current.CompletedStages.ShouldBe(32);
            current.Standings.Count.ShouldBe(32);
            current.Standings.Single(s => s.IsChampion).SeasonRank.ShouldBe(1);

            // Completed season table matches current final view.
            GetSeasonTableHandler table = new(store);
            GetSeasonTableResponse seasonTable = await table.HandleAsync(saveId, first, 1);
            seasonTable.IsSeasonComplete.ShouldBeTrue();
            seasonTable.SeasonChecksum.ShouldBe(current.SeasonChecksum);
            seasonTable.Standings.Select(s => s.AthleteId).ShouldBe(current.Standings.Select(s => s.AthleteId).ToList());
            seasonTable.Standings.Select(s => s.SeasonRank).ShouldBe(Enumerable.Range(1, 32).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertFinalLeagueAsync(SaveDbContext context, Guid saveId, int seasonId, int leagueId)
    {
        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.LeagueId == leagueId)
            .OrderBy(e => e.SeasonRank)
            .ToListAsync()
            .ConfigureAwait(false);
        rows.Count.ShouldBe(32);
        rows.Select(r => r.SeasonRank).ShouldBe(Enumerable.Range(1, 32).ToList());
        rows.Count(r => r.IsChampion).ShouldBe(1);
        rows.Single(r => r.IsChampion).SeasonRank.ShouldBe(1);

        // Totals equal the sum of stage championship points across all 32 stages.
        Dictionary<int, int> summedChampionship = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == seasonId && e.LeagueId == leagueId)
            .GroupBy(e => e.SaveAthleteId)
            .ToDictionaryAsync(g => g.Key, g => g.Sum(e => e.ChampionshipPointsThousandths))
            .ConfigureAwait(false);
        foreach (SeasonStandingEntity row in rows)
        {
            row.TotalChampionshipPointsThousandths.ShouldBe(summedChampionship[row.SaveAthleteId]);

            List<int>? stageCounts = JsonSerializer.Deserialize<List<int>>(row.StagePlaceCountsJson);
            stageCounts.ShouldNotBeNull();
            stageCounts.Count.ShouldBe(32);
            stageCounts.Sum().ShouldBe(32);
            stageCounts[0].ShouldBe(row.StageWins);

            List<int>? roundCounts = JsonSerializer.Deserialize<List<int>>(row.RoundPlaceCountsJson);
            roundCounts.ShouldNotBeNull();
            roundCounts.Count.ShouldBe(32);
            roundCounts.Sum().ShouldBe(32 * 16);
            roundCounts[0].ShouldBe(row.RoundWins);
        }
    }

    private static async Task<ulong> LoadRngStateAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return (ulong)rng.State;
        }
    }

    private static async Task<List<int>> AllLeagueIdsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Leagues.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-season-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
