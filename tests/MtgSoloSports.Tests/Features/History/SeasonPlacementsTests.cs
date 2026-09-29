using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.History.GetSeasonPlacements;
using MtgSoloSports.Features.History.GetStageStandings;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.History;

public sealed class SeasonPlacementsTests
{
    [Fact]
    public async Task Placements_EmptySeason_ReturnsRosterWithNoCells()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Placements Empty", 1101UL, 1102UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            GetSeasonPlacementsHandler placements = new(store);
            GetSeasonPlacementsResponse response = await placements.HandleAsync(saveId, 1, leagueId);

            response.SeasonNumber.ShouldBe(1);
            response.LeagueId.ShouldBe(leagueId);
            response.CompletedStages.ShouldBe(0);
            response.Athletes.Count.ShouldBe(32);
            response.Athletes.Select(a => a.AthleteId).Distinct().Count().ShouldBe(32);
            response.Placements.ShouldBeEmpty();
            response.IsSeasonComplete.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Placements_TwoCompletedStages_AlignExactlyWithStageStandings()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Placements Align", 1201UL, 1202UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            CompleteStageHandler complete = new(store);
            await CompleteStageForAllLeaguesAsync(store, saveId, complete, 1);
            await CompleteStageForAllLeaguesAsync(store, saveId, complete, 2);

            GetSeasonPlacementsHandler placements = new(store);
            GetSeasonPlacementsResponse matrix = await placements.HandleAsync(saveId, 1, leagueId);
            AssertTwoStagesPresent(matrix);

            GetHistoryStageStandingsHandler standings = new(store);
            GetHistoryStageStandingsResponse stageOne = await standings.HandleAsync(saveId, 1, leagueId, 1);
            GetHistoryStageStandingsResponse stageTwo = await standings.HandleAsync(saveId, 1, leagueId, 2);
            AssertCellsMatchStage(matrix, stageOne, 1);
            AssertCellsMatchStage(matrix, stageTwo, 2);
            AssertRanksCoverFullField(matrix);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertTwoStagesPresent(GetSeasonPlacementsResponse matrix)
    {
        matrix.CompletedStages.ShouldBe(2);
        matrix.Athletes.Count.ShouldBe(32);
        matrix.Placements.Count.ShouldBe(2 * 32);
    }

    private static void AssertCellsMatchStage(GetSeasonPlacementsResponse matrix, GetHistoryStageStandingsResponse stage, int stageNumber)
    {
        stage.Standings.Count.ShouldBe(32);
        foreach (HistoryStageStandingEntry want in stage.Standings.Take(5))
        {
            SeasonPlacementCell got = matrix.Placements.Single(c => c.AthleteId == want.AthleteId && c.StageNumber == stageNumber);
            got.StageRank.ShouldBe(want.StageRank);
            got.EarnedBonusThousandths.ShouldBe(want.EarnedBonusThousandths);
            got.ChampionshipPointsThousandths.ShouldBe(want.ChampionshipPointsThousandths);
        }

        matrix.Placements
            .Where(c => c.StageNumber == stageNumber)
            .Select(c => c.StageRank)
            .OrderBy(r => r)
            .ShouldBe(Enumerable.Range(1, 32).ToList());
        matrix.Placements.Any(c => c.StageNumber == stageNumber + 2 && stageNumber == 1).ShouldBeFalse();
        HashSet<int> roster = matrix.Athletes.Select(a => a.AthleteId).ToHashSet();
        foreach (SeasonPlacementCell cell in matrix.Placements.Where(c => c.StageNumber == stageNumber))
        {
            roster.ShouldContain(cell.AthleteId);
        }
    }

    private static void AssertRanksCoverFullField(GetSeasonPlacementsResponse matrix)
    {
        matrix.Placements.Any(c => c.StageNumber == 3).ShouldBeFalse();
    }

    [Fact]
    public async Task Placements_IncompleteCurrentStage_StaysBlank()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Placements Incomplete", 1301UL, 1302UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            CompleteStageHandler complete = new(store);
            await CompleteStageForAllLeaguesAsync(store, saveId, complete, 1);

            // Start stage 2 with a single persisted round but no standings.
            AdvanceRoundHandler advance = new(store);
            AdvanceRoundResponse round = await advance.HandleAsync(saveId, leagueId);
            round.StageNumber.ShouldBe(2);

            GetSeasonPlacementsHandler placements = new(store);
            GetSeasonPlacementsResponse matrix = await placements.HandleAsync(saveId, 1, leagueId);

            matrix.CompletedStages.ShouldBe(1);
            matrix.Placements.Count.ShouldBe(32);
            matrix.Placements.Any(c => c.StageNumber == 2).ShouldBeFalse();
            matrix.Athletes.Count.ShouldBe(32);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Placements_HistoricalSeasonSwitching_KeepsSeasonSpecificRosters()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Placements History", 1401UL, 1402UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int seasonOneLeague = await FirstLeagueIdAsync(store, saveId);

            GetSeasonPlacementsHandler placements = new(store);
            GetSeasonPlacementsResponse before = await placements.HandleAsync(saveId, 1, seasonOneLeague);
            before.CompletedStages.ShouldBe(0);

            SimulateSeasonsHandler bulk = new(store);
            SimulateSeasonsResponse simulated = await bulk.HandleAsync(saveId, new SimulateSeasonsRequest(1));
            simulated.SeasonsCompleted.ShouldBe(1);

            // Season 1 is now complete: all 32 stages persisted and immutable.
            GetSeasonPlacementsResponse seasonOne = await placements.HandleAsync(saveId, 1, seasonOneLeague);
            seasonOne.CompletedStages.ShouldBe(32);
            seasonOne.Placements.Count.ShouldBe(32 * 32);
            seasonOne.IsSeasonComplete.ShouldBeTrue();

            // Old completed stages never change because new seasons ran.
            GetHistoryStageStandingsHandler standings = new(store);
            GetHistoryStageStandingsResponse stageOne = await standings.HandleAsync(saveId, 1, seasonOneLeague, 1);
            SeasonPlacementCell cell = seasonOne.Placements.Single(c => c.StageNumber == 1 && c.StageRank == 1);
            HistoryStageStandingEntry winner = stageOne.Standings.Single(s => s.StageRank == 1);
            cell.AthleteId.ShouldBe(winner.AthleteId);
            cell.EarnedBonusThousandths.ShouldBe(winner.EarnedBonusThousandths);

            // Season 2 has its own league rows and its own roster.
            ListHistoryCompetitionsHandler competitions = new(store);
            ListHistoryCompetitionsResponse seasonTwoCompetitions = await competitions.HandleAsync(saveId, 2);
            seasonTwoCompetitions.Competitions.Count.ShouldBeGreaterThanOrEqualTo(8);
            int seasonTwoLeague = seasonTwoCompetitions.Competitions.OrderBy(c => c.LeagueId).First().LeagueId;

            GetSeasonPlacementsResponse seasonTwo = await placements.HandleAsync(saveId, 2, seasonTwoLeague);
            seasonTwo.SeasonNumber.ShouldBe(2);
            seasonTwo.Athletes.Count.ShouldBe(32);
            seasonTwo.CompletedStages.ShouldBe(0);
            seasonTwo.Placements.ShouldBeEmpty();

            // Rosters are season-specific: league ids differ across seasons.
            seasonTwoLeague.ShouldNotBe(seasonOneLeague);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Placements_Errors_InvalidIdsAndUnknownSeasonOrLeague()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Placements Errors", 1501UL, 1502UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            GetSeasonPlacementsHandler placements = new(store);
            await Should.ThrowAsync<ArgumentException>(() => placements.HandleAsync(Guid.Empty, 1, leagueId));
            await Should.ThrowAsync<ArgumentException>(() => placements.HandleAsync(saveId, 0, leagueId));
            await Should.ThrowAsync<ArgumentException>(() => placements.HandleAsync(saveId, 1, 0));
            await Should.ThrowAsync<SaveNotFoundException>(() => placements.HandleAsync(Guid.NewGuid(), 1, leagueId));
            await Should.ThrowAsync<HistoryNotFoundException>(() => placements.HandleAsync(saveId, 99, leagueId));
            await Should.ThrowAsync<HistoryNotFoundException>(() => placements.HandleAsync(saveId, 1, 999999));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Placements_DoesNotMutateSaveState()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Placements Readonly", 1601UL, 1602UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            CompleteStageHandler complete = new(store);
            await complete.HandleAsync(saveId, leagueId);

            (ulong rngBeforeState, ulong rngBeforeStream) = await LoadRngAsync(store, saveId);
            int standingsBefore = await CountStageStandingsAsync(store, saveId);

            GetSeasonPlacementsHandler placements = new(store);
            await placements.HandleAsync(saveId, 1, leagueId);
            await placements.HandleAsync(saveId, 1, leagueId);

            (ulong rngAfterState, ulong rngAfterStream) = await LoadRngAsync(store, saveId);
            rngAfterState.ShouldBe(rngBeforeState);
            rngAfterStream.ShouldBe(rngBeforeStream);
            (await CountStageStandingsAsync(store, saveId)).ShouldBe(standingsBefore);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CompleteStageForAllLeaguesAsync(SaveStore store, Guid saveId, CompleteStageHandler complete, int expectedStage)
    {
        List<int> leagues = await AllLeagueIdsAsync(store, saveId).ConfigureAwait(false);
        foreach (int leagueId in leagues)
        {
            CompleteStageResponse completed = await complete.HandleAsync(saveId, leagueId).ConfigureAwait(false);
            completed.StageNumber.ShouldBe(expectedStage);
        }
    }

    private static async Task<List<int>> AllLeagueIdsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Leagues.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync().ConfigureAwait(false);
    }

    private static async Task<int> FirstLeagueIdAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        LeagueEntity league = await context.Leagues.AsNoTracking().OrderBy(e => e.Id).FirstAsync().ConfigureAwait(false);
        return league.Id;
    }

    private static async Task<(ulong State, ulong Stream)> LoadRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return ((ulong)rng.State, (ulong)rng.Stream);
        }
    }

    private static async Task<int> CountStageStandingsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.StageStandings.AsNoTracking().CountAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-placements-" + Guid.NewGuid().ToString("N"));
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
