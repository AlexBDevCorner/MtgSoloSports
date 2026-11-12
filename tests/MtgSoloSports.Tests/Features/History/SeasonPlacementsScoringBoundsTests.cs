using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.History.GetSeasonPlacements;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.History;

/// <summary>
/// MSS-142: the placements contract carries the save-owned scoring bounds and
/// season size so clinch math never hardcodes 77/1/32. Read-only: bounds come
/// from the rules snapshot, completed counts ignore in-progress rounds, and
/// reads never touch RNG or standings.
/// </summary>
public sealed class SeasonPlacementsScoringBoundsTests
{
    [Fact]
    public async Task Placements_ExposesAuthoritativeScoringBounds_FromSnapshot()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Clinch Bounds", 2101UL, 2102UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            GetSeasonPlacementsHandler handler = new(store);
            GetSeasonPlacementsResponse response = await handler.HandleAsync(saveId, 1, leagueId);

            RulesV1 rules = await LoadRulesAsync(store, saveId);
            response.StagesPerSeason.ShouldBe(rules.StagesPerSeason);
            response.LeagueSize.ShouldBe(rules.LeagueSize);
            response.MaxStagePointsThousandths.ShouldBe(rules.ScoringTable.Max() * RulesV1.FixedScale);
            response.MinStagePointsThousandths.ShouldBe(rules.ScoringTable.Min() * RulesV1.FixedScale);
            response.MaxStagePointsThousandths.ShouldBe(77 * RulesV1.FixedScale);
            response.MinStagePointsThousandths.ShouldBe(1 * RulesV1.FixedScale);
            response.StagesPerSeason.ShouldBe(32);
            response.LeagueSize.ShouldBe(32);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Placements_BoundsStayConstant_AsOfficialStagesComplete()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Clinch Stable", 2201UL, 2202UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            GetSeasonPlacementsHandler handler = new(store);
            GetSeasonPlacementsResponse before = await handler.HandleAsync(saveId, 1, leagueId);
            before.CompletedStages.ShouldBe(0);

            CompleteStageHandler complete = new(store);
            await CompleteStageForAllLeaguesAsync(store, saveId, complete, 1);
            await CompleteStageForAllLeaguesAsync(store, saveId, complete, 2);

            GetSeasonPlacementsResponse after = await handler.HandleAsync(saveId, 1, leagueId);
            after.CompletedStages.ShouldBe(2);
            after.IsSeasonComplete.ShouldBeFalse();
            after.StagesPerSeason.ShouldBe(before.StagesPerSeason);
            after.LeagueSize.ShouldBe(before.LeagueSize);
            after.MaxStagePointsThousandths.ShouldBe(before.MaxStagePointsThousandths);
            after.MinStagePointsThousandths.ShouldBe(before.MinStagePointsThousandths);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Placements_IncompleteStage_DoesNotAdvanceCompletedCount_AndKeepsBounds()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Clinch Incomplete", 2301UL, 2302UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            CompleteStageHandler complete = new(store);
            await CompleteStageForAllLeaguesAsync(store, saveId, complete, 1);

            AdvanceRoundHandler advance = new(store);
            await advance.HandleAsync(saveId, leagueId);

            GetSeasonPlacementsHandler handler = new(store);
            GetSeasonPlacementsResponse response = await handler.HandleAsync(saveId, 1, leagueId);
            response.CompletedStages.ShouldBe(1);
            response.Placements.Any(c => c.StageNumber == 2).ShouldBeFalse();
            response.StagesPerSeason.ShouldBe(32);
            response.MaxStagePointsThousandths.ShouldBe(77_000);
            response.MinStagePointsThousandths.ShouldBe(1_000);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Placements_CompleteSeason_ReportsFullStages_WithStableBounds()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Clinch Final", 2401UL, 2402UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            SimulateSeasonsHandler bulk = new(store);
            await bulk.HandleAsync(saveId, new SimulateSeasonsRequest(1));

            GetSeasonPlacementsHandler handler = new(store);
            GetSeasonPlacementsResponse response = await handler.HandleAsync(saveId, 1, leagueId);
            response.IsSeasonComplete.ShouldBeTrue();
            response.CompletedStages.ShouldBe(response.StagesPerSeason);
            response.CompletedStages.ShouldBe(32);
            response.MaxStagePointsThousandths.ShouldBe(77_000);
            response.MinStagePointsThousandths.ShouldBe(1_000);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public void Placements_MapResponse_DerivesBoundsFromRules_NotLiterals()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        int wantMax = rules.ScoringTable.Max() * RulesV1.FixedScale;
        int wantMin = rules.ScoringTable.Min() * RulesV1.FixedScale;
        GetSeasonPlacementsHandler.MaxStagePoints(rules).ShouldBe(wantMax);
        GetSeasonPlacementsHandler.MinStagePoints(rules).ShouldBe(wantMin);
    }

    [Fact]
    public async Task Placements_DoesNotMutateRngOrStandings_WhenReadingBounds()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Clinch Readonly", 2501UL, 2502UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            CompleteStageHandler complete = new(store);
            await complete.HandleAsync(saveId, leagueId);

            (ulong stateBefore, ulong streamBefore) = await LoadRngAsync(store, saveId);
            int standingsBefore = await CountStageStandingsAsync(store, saveId);

            GetSeasonPlacementsHandler handler = new(store);
            GetSeasonPlacementsResponse first = await handler.HandleAsync(saveId, 1, leagueId);
            GetSeasonPlacementsResponse second = await handler.HandleAsync(saveId, 1, leagueId);
            second.MaxStagePointsThousandths.ShouldBe(first.MaxStagePointsThousandths);
            second.MinStagePointsThousandths.ShouldBe(first.MinStagePointsThousandths);

            (ulong stateAfter, ulong streamAfter) = await LoadRngAsync(store, saveId);
            stateAfter.ShouldBe(stateBefore);
            streamAfter.ShouldBe(streamBefore);
            (await CountStageStandingsAsync(store, saveId)).ShouldBe(standingsBefore);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
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

    private static async Task<RulesV1> LoadRulesAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await AdvanceRoundHandler.LoadRulesAsync(context, default).ConfigureAwait(false);
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-clinch-" + Guid.NewGuid().ToString("N"));
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
