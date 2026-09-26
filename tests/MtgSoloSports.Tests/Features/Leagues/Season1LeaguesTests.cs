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
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Leagues;

public sealed class Season1LeaguesTests
{
    [Fact]
    public async Task SameSeed_ProducesIdenticalLeagues_Golden()
    {
        var (store, root) = CreateStore();
        try
        {
            IReadOnlyList<CatalogAthlete> catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord first = await store.CreateAsync("League Golden One", 9001UL, 7002UL, catalog);
            SaveStore.CreationRecord second = await store.CreateAsync("League Golden Two", 9001UL, 7002UL, catalog);

            GetSeason1LeaguesResponse firstLeagues = await GetLeaguesAsync(store, first.Detail.SaveId);
            GetSeason1LeaguesResponse secondLeagues = await GetLeaguesAsync(store, second.Detail.SaveId);

            firstLeagues.DrawChecksum.ShouldBe(secondLeagues.DrawChecksum);
            firstLeagues.DrawChecksum.Length.ShouldBe(64);
            RosterNames(firstLeagues).ShouldBe(RosterNames(secondLeagues));

            Pcg32V1 fresh = new(9001UL, 7002UL);
            UniverseSelection universe = UniverseSelector.Select(catalog, fresh, RulesV1.CreateDefault());
            InauguralDrawResult draw = InauguralDrawSelector.Select(universe.Selected, fresh, RulesV1.CreateDefault());
            draw.Checksum.ShouldBe(firstLeagues.DrawChecksum);
            fresh.Snapshot().State.ShouldBe(first.Detail.RngState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DifferentSeed_ProducesDifferentButValidLeagues()
    {
        var (store, root) = CreateStore();
        try
        {
            IReadOnlyList<CatalogAthlete> catalog = UniverseTestCatalog.Build(perColor: 300);
            SaveStore.CreationRecord first = await store.CreateAsync("League Seed A", 11UL, 12UL, catalog);
            SaveStore.CreationRecord second = await store.CreateAsync("League Seed B", 13UL, 14UL, catalog);

            GetSeason1LeaguesResponse firstLeagues = await GetLeaguesAsync(store, first.Detail.SaveId);
            GetSeason1LeaguesResponse secondLeagues = await GetLeaguesAsync(store, second.Detail.SaveId);

            AssertValidSeason1(firstLeagues);
            AssertValidSeason1(secondLeagues);
            string.Equals(secondLeagues.DrawChecksum, firstLeagues.DrawChecksum, StringComparison.Ordinal).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Season1_HasEightLeagues_NoSuperleague_256Active()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("League Counts", 55UL, 66UL, UniverseTestCatalog.Build());
            GetSeason1LeaguesResponse response = await GetLeaguesAsync(store, created.Detail.SaveId);

            response.SeasonNumber.ShouldBe(1);
            response.HasSuperleague.ShouldBeFalse();
            response.Leagues.Count.ShouldBe(8);
            response.ActiveAthletes.ShouldBe(256);
            response.PoolAthletes.ShouldBe(1792);
            response.PoolCounts.Count.ShouldBe(8);
            response.PoolCounts.Sum(p => p.Count).ShouldBe(1792);

            foreach (Season1LeagueRoster league in response.Leagues)
            {
                league.Athletes.Count.ShouldBe(32);
            }

            foreach (Season1PoolCount pool in response.PoolCounts)
            {
                pool.Count.ShouldBe(224);
            }

            IReadOnlyList<string> active = RosterNames(response);
            active.Count.ShouldBe(256);
            active.Distinct(StringComparer.Ordinal).Count().ShouldBe(256);

            await AssertMembershipCoverageAsync(store, created.Detail.SaveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LeagueRosters_MatchSportingColor_AndDrawOrder()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("League Colors", 77UL, 88UL, UniverseTestCatalog.Build());
            GetSeason1LeaguesResponse response = await GetLeaguesAsync(store, created.Detail.SaveId);

            Dictionary<string, SportingColor> colorsByName = UniverseTestCatalog.Build()
                .ToDictionary(a => a.Name, a => a.SportingColor, StringComparer.Ordinal);
            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            Dictionary<string, int> persistedColor = await context.SaveAthletes
                .AsNoTracking()
                .ToDictionaryAsync(e => e.Name, e => e.SportingColor, StringComparer.Ordinal);

            foreach (Season1LeagueRoster league in response.Leagues)
            {
                SportingColor expected = Enum.Parse<SportingColor>(league.SportingColor);
                List<int> indices = league.Athletes.Select(a => a.DrawIndex).ToList();
                indices.ShouldBe(Enumerable.Range(0, 32).ToList());
                foreach (Season1RosterAthlete athlete in league.Athletes)
                {
                    persistedColor[athlete.Name].ShouldBe((int)expected);
                    colorsByName[athlete.Name].ShouldBe(expected);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DrawResult_Persisted_AndReplayable_WithoutResimulation()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("League Replay", 101UL, 202UL, UniverseTestCatalog.Build());
            GetSeason1LeaguesResponse first = await GetLeaguesAsync(store, created.Detail.SaveId);
            GetSeason1LeaguesResponse second = await GetLeaguesAsync(store, created.Detail.SaveId);

            second.DrawChecksum.ShouldBe(first.DrawChecksum);
            second.ActiveAthletes.ShouldBe(first.ActiveAthletes);
            second.PoolAthletes.ShouldBe(first.PoolAthletes);
            RosterNames(second).ShouldBe(RosterNames(first));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAthletes_RemainCommonPool_MembershipIsSourceOfTruth()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("League Truth", 303UL, 404UL, UniverseTestCatalog.Build());
            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            int pooled = await context.SaveAthletes.CountAsync(e => e.Status == (int)SaveAthleteStatus.CommonPool);
            pooled.ShouldBe(2048);

            int memberships = await context.SeasonMemberships.CountAsync(e => e.SeasonId == 1);
            memberships.ShouldBe(2048);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownSave_ThrowsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            GetSeason1LeaguesHandler handler = new(store);
            await Should.ThrowAsync<SaveNotFoundException>(() => handler.HandleAsync(Guid.NewGuid()));
            await Should.ThrowAsync<ArgumentException>(() => handler.HandleAsync(Guid.Empty));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Selector_ExactQuotas_DrawsAllColorsDeterministically()
    {
        IReadOnlyList<CatalogAthlete> population = UniverseTestCatalog.Build(perColor: 256);
        RulesV1 rules = RulesV1.CreateDefault();

        Pcg32V1 firstRng = new(1UL, 2UL);
        InauguralDrawResult first = InauguralDrawSelector.Select(population, firstRng, rules);

        Pcg32V1 secondRng = new(1UL, 2UL);
        InauguralDrawResult second = InauguralDrawSelector.Select(population, secondRng, rules);

        first.Entries.Count.ShouldBe(2048);
        first.Checksum.ShouldBe(second.Checksum);
        first.Checksum.Length.ShouldBe(64);
        Season1Invariants.ValidateDrawResult(first.Entries, rules);
        InauguralDrawSelector.ComputeChecksum(first.Entries).ShouldBe(first.Checksum);
    }

    [Fact]
    public void Selector_Checksum_DetectsRosterChange_IgnoresInputOrder()
    {
        IReadOnlyList<CatalogAthlete> population = UniverseTestCatalog.Build(perColor: 256);
        InauguralDrawResult draw = InauguralDrawSelector.Select(population, new Pcg32V1(9UL, 9UL), RulesV1.CreateDefault());

        List<InauguralDrawEntry> reordered = [.. draw.Entries];
        reordered.Reverse();
        InauguralDrawSelector.ComputeChecksum(reordered).ShouldBe(draw.Checksum);

        List<InauguralDrawEntry> changed = [.. draw.Entries];
        int victimIndex = changed.FindIndex(e => e.IsLeagueMember);
        InauguralDrawEntry victim = changed[victimIndex];
        changed[victimIndex] = victim with { Name = victim.Name + " Changed" };
        string.Equals(InauguralDrawSelector.ComputeChecksum(changed), draw.Checksum, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void Invariants_RejectCorruptedDraws()
    {
        IReadOnlyList<CatalogAthlete> population = UniverseTestCatalog.Build(perColor: 256);
        RulesV1 rules = RulesV1.CreateDefault();
        InauguralDrawResult draw = InauguralDrawSelector.Select(population, new Pcg32V1(5UL, 6UL), rules);

        List<InauguralDrawEntry> duplicated = [.. draw.Entries];
        duplicated[0] = duplicated[1];
        Should.Throw<InvalidOperationException>(() => Season1Invariants.ValidateDrawResult(duplicated, rules));

        Should.Throw<InvalidOperationException>(() => Season1Invariants.ValidateDrawResult(draw.Entries.Take(2047).ToList(), rules));

        List<InauguralDrawEntry> badIndex = [.. draw.Entries];
        InauguralDrawEntry victim = badIndex.First(e => e.IsLeagueMember);
        badIndex[badIndex.IndexOf(victim)] = victim with { DrawIndex = 200 };
        Should.Throw<InvalidOperationException>(() => Season1Invariants.ValidateDrawResult(badIndex, rules));

        Should.Throw<InvalidOperationException>(() => InauguralDrawSelector.Select(UniverseTestCatalog.Build(perColor: 10), new Pcg32V1(1UL, 2UL), rules));
    }

    [Fact]
    public void PersistedInvariants_RejectCorruptedState()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<PersistedLeague> leagues = BuildPersistedLeagues();
        List<PersistedMembership> memberships = BuildPersistedMemberships(leagues, rules);

        Season1PersistedInvariants.ValidatePersistedSeason1(1, false, leagues, memberships, 2048, rules);

        Should.Throw<InvalidOperationException>(() => Season1PersistedInvariants.ValidatePersistedSeason1(1, true, leagues, memberships, 2048, rules));
        Should.Throw<InvalidOperationException>(() => Season1PersistedInvariants.ValidatePersistedSeason1(2, false, leagues, memberships, 2048, rules));
        Should.Throw<InvalidOperationException>(() => Season1PersistedInvariants.ValidatePersistedSeason1(1, false, leagues.Take(7).ToList(), memberships, 2048, rules));

        List<PersistedMembership> duplicated = [.. memberships, memberships[0]];
        Should.Throw<InvalidOperationException>(() => Season1PersistedInvariants.ValidatePersistedSeason1(1, false, leagues, duplicated, 2049, rules));

        PersistedMembership victim = memberships.First(m => m.LeagueId is not null);
        List<PersistedMembership> colorMismatch = memberships.Select(m => m == victim ? m with { SportingColor = NextColor(m.SportingColor) } : m).ToList();
        Should.Throw<InvalidOperationException>(() => Season1PersistedInvariants.ValidatePersistedSeason1(1, false, leagues, colorMismatch, 2048, rules));
    }

    private static void AssertValidSeason1(GetSeason1LeaguesResponse response)
    {
        response.SeasonNumber.ShouldBe(1);
        response.HasSuperleague.ShouldBeFalse();
        response.Leagues.Count.ShouldBe(8);
        response.ActiveAthletes.ShouldBe(256);
        response.PoolAthletes.ShouldBe(1792);
        foreach (Season1LeagueRoster league in response.Leagues)
        {
            league.Athletes.Count.ShouldBe(32);
        }
    }

    private static IReadOnlyList<string> RosterNames(GetSeason1LeaguesResponse response)
    {
        return response.Leagues.SelectMany(l => l.Athletes).Select(a => a.Name).ToList();
    }

    private static async Task<GetSeason1LeaguesResponse> GetLeaguesAsync(SaveStore store, Guid saveId)
    {
        GetSeason1LeaguesHandler handler = new(store);
        return await handler.HandleAsync(saveId).ConfigureAwait(false);
    }

    private static async Task AssertMembershipCoverageAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships.AsNoTracking().ToListAsync().ConfigureAwait(false);
        memberships.Count.ShouldBe(2048);
        memberships.Select(m => m.SaveAthleteId).Distinct().Count().ShouldBe(2048);
        memberships.Count(m => m.LeagueId is not null).ShouldBe(256);
        memberships.Count(m => m.LeagueId is null).ShouldBe(1792);

        List<LeagueEntity> leagues = await context.Leagues.AsNoTracking().ToListAsync().ConfigureAwait(false);
        leagues.Count.ShouldBe(8);
        leagues.All(l => l.Kind == (int)LeagueKind.Feeder).ShouldBeTrue();
    }

    private static List<PersistedLeague> BuildPersistedLeagues()
    {
        List<PersistedLeague> leagues = [];
        int id = 1;
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            leagues.Add(new PersistedLeague(id++, color, 0, $"{color} League"));
        }

        return leagues;
    }

    private static List<PersistedMembership> BuildPersistedMemberships(IReadOnlyList<PersistedLeague> leagues, RulesV1 rules)
    {
        Dictionary<SportingColor, int> leagueIds = leagues.ToDictionary(l => l.SportingColor, l => l.LeagueId);
        List<PersistedMembership> memberships = new(rules.TotalAthletesInSave);
        int athleteId = 1;
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            for (int draw = 0; draw < rules.AthletesPerSportingColor; draw++)
            {
                int? leagueId = draw < rules.LeagueSize ? leagueIds[color] : null;
                memberships.Add(new PersistedMembership(athleteId++, color, leagueId, draw));
            }
        }

        return memberships;
    }

    private static SportingColor NextColor(SportingColor color)
    {
        return color == SportingColor.White ? SportingColor.Blue : SportingColor.White;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-season1-" + Guid.NewGuid().ToString("N"));
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
