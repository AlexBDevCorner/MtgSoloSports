using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Features.Leagues.GetSeason1Leagues;
using MtgSoloSports.Features.Leagues.InauguralDraw;
using MtgSoloSports.Features.Universe.CreateUniverse;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Leagues;

/// <summary>
/// MSS-057 deterministic Season 1 tiered-draw coverage: F1/F2/F3 per color,
/// exact per-color/global counts, F1-only inaugural source, and
/// seed/catalog/rules reproducibility.
/// </summary>
public sealed class Season1TieredDrawTests
{
    [Fact]
    public async Task NewTieredSave_Has24Feeders_768Active_160PoolPerColor()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Tiered Counts", 4242UL, 5656UL, UniverseTestCatalog.Build());
            created.Detail.RulesVersion.ShouldBe(RulesV2.RulesVersion);

            GetSeason1LeaguesHandler handler = new(store);
            GetSeason1LeaguesResponse response = await handler.HandleAsync(created.Detail.SaveId);

            AssertTieredCounts(response);
            await AssertMembershipCoverageAsync(store, created.Detail.SaveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TieredSelector_IsDeterministic_PerColorSplit()
    {
        IReadOnlyList<CatalogAthlete> population = UniverseTestCatalog.Build(perColor: 256);
        RulesV2 rules = RulesV2.CreateDefault();

        Pcg32V1 firstRng = new(1234UL, 5678UL);
        InauguralDrawResult first = InauguralDrawSelector.Select(population, firstRng, rules);

        Pcg32V1 secondRng = new(1234UL, 5678UL);
        InauguralDrawResult second = InauguralDrawSelector.Select(population, secondRng, rules);

        first.Checksum.ShouldBe(second.Checksum);
        Season1Invariants.ValidateDrawResult(first.Entries, rules);
        AssertPerColorSplit(first, rules);
    }

    [Fact]
    public void TieredSelector_EquivalentSeed_ReproducesSameAssignment()
    {
        IReadOnlyList<CatalogAthlete> catalog = UniverseTestCatalog.Build();
        RulesV2 rules = RulesV2.CreateDefault();

        Pcg32V1 rngA = new(9001UL, 7002UL);
        UniverseSelection universeA = UniverseSelector.Select(catalog, rngA, rules);
        InauguralDrawResult drawA = InauguralDrawSelector.Select(universeA.Selected, rngA, rules);

        Pcg32V1 rngB = new(9001UL, 7002UL);
        UniverseSelection universeB = UniverseSelector.Select(catalog, rngB, rules);
        InauguralDrawResult drawB = InauguralDrawSelector.Select(universeB.Selected, rngB, rules);

        drawA.Checksum.ShouldBe(drawB.Checksum);
        drawA.Entries.Select(e => e.Name).ShouldBe(drawB.Entries.Select(e => e.Name).ToList());
    }

    [Fact]
    public void V1Selector_StillSingleTier_ForCompatibility()
    {
        IReadOnlyList<CatalogAthlete> population = UniverseTestCatalog.Build(perColor: 256);
        RulesV1 rules = RulesV1.CreateDefault();

        InauguralDrawResult draw = InauguralDrawSelector.Select(population, new Pcg32V1(5UL, 6UL), rules);
        Season1Invariants.ValidateDrawResult(draw.Entries, rules);

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            draw.Entries.Count(e => e.SportingColor == color && e.IsLeagueMember).ShouldBe(32);
            draw.Entries.Count(e => e.SportingColor == color && !e.IsLeagueMember).ShouldBe(224);
        }
    }

    [Fact]
    public void InauguralSelection_Tiered_ReadsF1Only()
    {
        RulesV2 rules = RulesV2.CreateDefault();
        List<LeagueEntity> feeders = BuildTieredFeeders();
        List<SeasonStandingEntity> standings = BuildStandings(feeders);

        IReadOnlyList<MtgSoloSports.Features.Superleague.CreateInaugural.InauguralSuperleagueSelection.InauguralPick> picks =
            MtgSoloSports.Features.Superleague.CreateInaugural.InauguralSuperleagueSelection.Select(standings, feeders, rules);

        picks.Count.ShouldBe(32);
        HashSet<int> f1Ids = feeders.Where(l => l.FeederDivision == (int)FeederDivision.First).Select(l => l.Id).ToHashSet();
        picks.All(p => f1Ids.Contains(p.FromLeagueId)).ShouldBeTrue();
    }

    private static void AssertTieredCounts(GetSeason1LeaguesResponse response)
    {
        response.SeasonNumber.ShouldBe(1);
        response.HasSuperleague.ShouldBeFalse();
        response.Leagues.Count.ShouldBe(24);
        response.ActiveAthletes.ShouldBe(768);
        response.PoolAthletes.ShouldBe(1280);

        foreach (Season1PoolCount pool in response.PoolCounts)
        {
            pool.Count.ShouldBe(160);
        }

        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            List<Season1LeagueRoster> colorLeagues = response.Leagues
                .Where(l => string.Equals(l.SportingColor, color.ToString(), StringComparison.Ordinal))
                .OrderBy(l => l.FeederDivision)
                .ToList();
            colorLeagues.Count.ShouldBe(3);
            colorLeagues.Select(l => l.FeederDivision).ShouldBe([1, 2, 3]);
            foreach (Season1LeagueRoster league in colorLeagues)
            {
                league.Athletes.Count.ShouldBe(32);
            }
        }

        List<string> active = response.Leagues.SelectMany(l => l.Athletes).Select(a => a.Name).ToList();
        active.Count.ShouldBe(768);
        active.Distinct(StringComparer.Ordinal).Count().ShouldBe(768);
    }

    private static void AssertPerColorSplit(InauguralDrawResult first, RulesV2 rules)
    {
        _ = rules;
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            List<InauguralDrawEntry> colorEntries = first.Entries.Where(e => e.SportingColor == color).ToList();
            colorEntries.Count.ShouldBe(256);
            colorEntries.Count(e => e.IsLeagueMember).ShouldBe(96);
            colorEntries.Count(e => !e.IsLeagueMember).ShouldBe(160);
        }
    }

    private static List<LeagueEntity> BuildTieredFeeders()
    {
        List<LeagueEntity> feeders = [];
        int leagueId = 1;
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            feeders.Add(new LeagueEntity { Id = leagueId++, SeasonId = 1, SportingColor = (int)color, Kind = (int)LeagueKind.Feeder, FeederDivision = (int)FeederDivision.First, Name = $"{color} League" });
            feeders.Add(new LeagueEntity { Id = leagueId++, SeasonId = 1, SportingColor = (int)color, Kind = (int)LeagueKind.Feeder, FeederDivision = (int)FeederDivision.Second, Name = $"{color} League F2" });
            feeders.Add(new LeagueEntity { Id = leagueId++, SeasonId = 1, SportingColor = (int)color, Kind = (int)LeagueKind.Feeder, FeederDivision = (int)FeederDivision.Third, Name = $"{color} League F3" });
        }

        return feeders;
    }

    private static List<SeasonStandingEntity> BuildStandings(List<LeagueEntity> feeders)
    {
        List<SeasonStandingEntity> standings = [];
        int athleteId = 1;
        foreach (LeagueEntity league in feeders)
        {
            for (int rank = 1; rank <= 32; rank++)
            {
                standings.Add(new SeasonStandingEntity
                {
                    Id = athleteId,
                    SeasonId = 1,
                    LeagueId = league.Id,
                    SaveAthleteId = athleteId++,
                    SeasonRank = rank,
                });
            }
        }

        return standings;
    }

    private static async Task AssertMembershipCoverageAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships.AsNoTracking().ToListAsync().ConfigureAwait(false);
        memberships.Count.ShouldBe(2048);
        memberships.Select(m => m.SaveAthleteId).Distinct().Count().ShouldBe(2048);
        memberships.Count(m => m.LeagueId is not null).ShouldBe(768);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-tiered-" + Guid.NewGuid().ToString("N"));
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
