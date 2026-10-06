using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.History.GetSeasonPlacements;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.Leagues.SeasonProgress;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Leagues;

public sealed class FeederDivisionPersistenceTests
{
    [Fact]
    public async Task NewSave_UsesV2Rules_AndFeeder1Leagues()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Tiered Save", 4242UL, 5656UL, UniverseTestCatalog.Build());

            created.Detail.RulesVersion.ShouldBe(RulesV2.RulesVersion);
            RulesV1 rules = RulesSnapshotCodec.Decode(created.Detail.RulesJson);
            rules.ShouldBeOfType<RulesV2>();

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            List<LeagueEntity> leagues = await context.Leagues.AsNoTracking().ToListAsync();
            leagues.Count.ShouldBe(24);
            leagues.Count(l => l.FeederDivision == (int)FeederDivision.First).ShouldBe(8);
            leagues.Count(l => l.FeederDivision == (int)FeederDivision.Second).ShouldBe(8);
            leagues.Count(l => l.FeederDivision == (int)FeederDivision.Third).ShouldBe(8);
            foreach (LeagueEntity league in leagues)
            {
                league.Kind.ShouldBe((int)LeagueKind.Feeder);
                LeagueEntityLevels.GetLevel(league).ShouldBe(
                    league.FeederDivision == (int)FeederDivision.First ? LeagueLevel.Feeder1 :
                    league.FeederDivision == (int)FeederDivision.Second ? LeagueLevel.Feeder2 :
                    LeagueLevel.Feeder3);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HistoricalV1Feeder_MapsToFeeder1()
    {
        // Rows persisted before the division column (stored None/0) represent
        // the single historical feeder tier.
        LeagueEntity legacy = new()
        {
            Id = 1,
            SeasonId = 1,
            SportingColor = 0,
            Kind = (int)LeagueKind.Feeder,
            FeederDivision = (int)FeederDivision.None,
            Name = "White League",
        };

        LeagueEntityLevels.GetLevel(legacy).ShouldBe(LeagueLevel.Feeder1);
        LeagueEntityLevels.IsFeeder(legacy).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public void Superleague_WithDivision_FailsFast()
    {
        LeagueEntity corrupt = new()
        {
            Id = 1,
            SeasonId = 1,
            SportingColor = 0,
            Kind = (int)LeagueKind.Superleague,
            FeederDivision = (int)FeederDivision.First,
            Name = "Superleague",
        };

        Should.Throw<InvalidOperationException>(() => LeagueEntityLevels.GetLevel(corrupt));
    }

    [Fact]
    public async Task ReadModels_CarryDivisionWithoutNameParsing()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Division Reads", 777UL, 888UL, UniverseTestCatalog.Build());

            // Complete one stage so history/placements have rows.
            CompleteStageHandler stages = new(store);
            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                List<LeagueEntity> leagues = await context.Leagues.ToListAsync();
                LeagueEntity first = leagues.OrderBy(l => l.Id).First();
                await stages.HandleAsync(created.Detail.SaveId, first.Id);
            }

            GetSeasonProgressHandler progress = new(store);
            GetSeasonProgressResponse progressResponse = await progress.HandleAsync(created.Detail.SaveId, 1);
            progressResponse.Leagues.Count.ShouldBe(24);
            progressResponse.Leagues.Count(l => l.FeederDivision == (int)FeederDivision.First).ShouldBe(8);
            progressResponse.Leagues.Count(l => l.FeederDivision == (int)FeederDivision.Second).ShouldBe(8);
            progressResponse.Leagues.Count(l => l.FeederDivision == (int)FeederDivision.Third).ShouldBe(8);
            foreach (SeasonProgressLeague league in progressResponse.Leagues)
            {
                (league.FeederDivision is 1 or 2 or 3).ShouldBeTrue();
            }

            ListHistoryCompetitionsHandler competitions = new(store);
            ListHistoryCompetitionsResponse competitionsResponse =
                await competitions.HandleAsync(created.Detail.SaveId, 1);
            competitionsResponse.Competitions.Count.ShouldBe(24);
            foreach (HistoryCompetitionSummary summary in competitionsResponse.Competitions)
            {
                summary.FeederDivision.ShouldBe((int)FeederDivision.First);
                summary.LeagueLevel.ShouldBe(nameof(LeagueLevel.Feeder1));
            }

            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                LeagueEntity league = await context.Leagues.AsNoTracking().OrderBy(l => l.Id).FirstAsync();
                GetSeasonPlacementsHandler placements = new(store);
                GetSeasonPlacementsResponse placementsResponse =
                    await placements.HandleAsync(created.Detail.SaveId, 1, league.Id);
                placementsResponse.FeederDivision.ShouldBe((int)FeederDivision.First);
                placementsResponse.LeagueLevel.ShouldBe(nameof(LeagueLevel.Feeder1));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-tier-" + Guid.NewGuid().ToString("N"));
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
