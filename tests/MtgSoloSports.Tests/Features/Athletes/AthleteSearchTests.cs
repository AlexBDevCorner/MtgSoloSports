using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Athletes.SearchAthletes;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Athletes;

public sealed class AthleteSearchTests
{
    [Fact]
    public void AggregateHonours_CountsLeaguePodiumsOnce()
    {
        IReadOnlyDictionary<int, SearchAthletesFilter.HonourTotals> totals = SearchAthletesFilter.AggregateHonours(
            [new(1, 1), new(1, 2), new(2, 4)],
            [],
            [],
            [],
            [],
            []);
        totals[1].Honours.ShouldBe(2);
        totals[1].Titles.ShouldBe(1);
        totals.ContainsKey(2).ShouldBeFalse();
    }

    [Fact]
    public void AggregateHonours_TeamLegCountsOnlyWhenTeamPodiums()
    {
        IReadOnlyDictionary<int, SearchAthletesFilter.HonourTotals> totals = SearchAthletesFilter.AggregateHonours(
            [],
            [],
            [new(7, 10, 0), new(8, 10, 1)],
            [new(10, 0, 2), new(10, 1, 5)],
            [],
            []);
        totals[7].Honours.ShouldBe(1);
        totals[7].Titles.ShouldBe(0);
        totals[7].CupAppearances.ShouldBe(1);
        totals[7].CupPodiums.ShouldBe(1);
        totals[8].Honours.ShouldBe(0);
        totals[8].CupAppearances.ShouldBe(1);
        totals[8].CupPodiums.ShouldBe(0);
    }

    [Fact]
    public void MatchesSearch_IsCaseInsensitivePartial()
    {
        SearchAthletesFilter.MatchesSearch("Blue Athlete 0007", "blue ath").ShouldBeTrue();
        SearchAthletesFilter.MatchesSearch("Blue Athlete 0007", "ATHLETE 0007").ShouldBeTrue();
        SearchAthletesFilter.MatchesSearch("Blue Athlete 0007", "red").ShouldBeFalse();
        SearchAthletesFilter.MatchesSearch("Blue Athlete 0007", " ").ShouldBeTrue();
    }

    [Fact]
    public void ApplyFilters_AndAcrossCategories_OrWithinCategory()
    {
        SearchAthletesFilter.Candidate active = BuildCandidate(1, "Alpha", 0, true, "White League", 3, ["Human"], 2, 1, 1);
        SearchAthletesFilter.Candidate pool = BuildCandidate(2, "Beta", 1, false, null, 0, ["Elf"], 0, 0, 0);
        IReadOnlyList<SearchAthletesFilter.Candidate> candidates = [active, pool];

        SearchAthletesQuery and = new(
            null, 3, null, null, null, ["Superleague"], 1, null, null, null, null, null, null, null, null, null, null, null, null);
        SearchAthletesFilter.ApplyFilters(candidates, and).Count.ShouldBe(0);

        SearchAthletesQuery match = new(
            "alp", 3, null, null, null, ["White League"], 1, null, null, null, null, null, null, null, null, null, null, null, null);
        IReadOnlyList<SearchAthletesFilter.Candidate> matched = SearchAthletesFilter.ApplyFilters(candidates, match);
        matched.Count.ShouldBe(1);
        matched[0].AthleteId.ShouldBe(1);

        SearchAthletesQuery colors = new(
            null, null, null, [0, 1], null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        SearchAthletesFilter.ApplyFilters(candidates, colors).Count.ShouldBe(2);

        SearchAthletesQuery titlesOnly = new(
            null, null, null, null, null, null, 1, null, null, "only", null, null, null, null, null, null, null, null, null);
        IReadOnlyList<SearchAthletesFilter.Candidate> titled = SearchAthletesFilter.ApplyFilters(candidates, titlesOnly);
        titled.Count.ShouldBe(1);
        titled[0].AthleteId.ShouldBe(1);

        SearchAthletesQuery noTitles = new(
            null, null, null, null, null, null, null, null, null, "none", null, null, null, null, null, null, null, null, null);
        IReadOnlyList<SearchAthletesFilter.Candidate> untitled = SearchAthletesFilter.ApplyFilters(candidates, noTitles);
        untitled.Count.ShouldBe(1);
        untitled[0].AthleteId.ShouldBe(2);
    }

    [Fact]
    public void ApplySort_IsDeterministic()
    {
        SearchAthletesFilter.Candidate a = BuildCandidate(2, "Beta", 0, true, "White League", 1, ["Human"], 1, 0, 0);
        SearchAthletesFilter.Candidate b = BuildCandidate(1, "Alpha", 0, true, "White League", 3, ["Human"], 3, 2, 1);
        IReadOnlyList<SearchAthletesFilter.Candidate> candidates = [a, b];

        IReadOnlyList<SearchAthletesFilter.Candidate> byName = SearchAthletesFilter.ApplySort(candidates, "name", "asc");
        byName[0].AthleteId.ShouldBe(1);

        IReadOnlyList<SearchAthletesFilter.Candidate> byNonPool = SearchAthletesFilter.ApplySort(candidates, "nonPool", "desc");
        byNonPool[0].AthleteId.ShouldBe(1);

        IReadOnlyList<SearchAthletesFilter.Candidate> byHonours = SearchAthletesFilter.ApplySort(candidates, "honours", "desc");
        byHonours[0].AthleteId.ShouldBe(1);
    }

    [Fact]
    public void ApplySort_BestFinishDescending_ReversesNumericOrderAndKeepsMissingLast()
    {
        SearchAthletesFilter.Candidate missing = BuildCandidate(1, "Missing", 0, true, "White League", 1, ["Human"], 0, 0, 0);
        SearchAthletesFilter.Candidate first = BuildCandidate(2, "First", 0, true, "White League", 1, ["Human"], 0, 0, 1);
        SearchAthletesFilter.Candidate fifth = BuildCandidate(3, "Fifth", 0, true, "White League", 1, ["Human"], 0, 0, 5);

        IReadOnlyList<SearchAthletesFilter.Candidate> sorted =
            SearchAthletesFilter.ApplySort([missing, first, fifth], "bestFinish", "desc");

        sorted.Select(e => e.AthleteId).ShouldBe([3, 2, 1]);
    }

    [Fact]
    public async Task Search_AfterTwoStages_NonPoolCountsSeasonOnce()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Search Stages", 4242UL, 8484UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            global::MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues.CompleteStageForAllLeaguesHandler bulk = new(store);
            await bulk.HandleAsync(saveId);
            await bulk.HandleAsync(saveId);

            await AssertTwoStageSearchAsync(store, saveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertTwoStageSearchAsync(SaveStore store, Guid saveId)
    {
        SearchAthletesHandler handler = new(store);
        SearchAthletesResponse activePage = await handler.HandleAsync(saveId, SearchAthletesQuery.Empty with { MinNonPoolSeasons = 1, Take = 2000 }).ConfigureAwait(false);
        activePage.TotalCount.ShouldBe(768);
        activePage.Results.All(e => e.NonPoolSeasons >= 1).ShouldBeTrue();

        using SaveDbContext context = store.OpenDbContext(saveId);
        int activeId = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.LeagueId != null)
            .OrderBy(e => e.SaveAthleteId)
            .Select(e => e.SaveAthleteId)
            .FirstAsync().ConfigureAwait(false);
        int poolId = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.LeagueId == null)
            .OrderBy(e => e.SaveAthleteId)
            .Select(e => e.SaveAthleteId)
            .FirstAsync().ConfigureAwait(false);
        string activeName = await context.SaveAthletes.AsNoTracking().Where(e => e.Id == activeId).Select(e => e.Name).SingleAsync().ConfigureAwait(false);
        string poolName = await context.SaveAthletes.AsNoTracking().Where(e => e.Id == poolId).Select(e => e.Name).SingleAsync().ConfigureAwait(false);

        SearchAthletesResponse activeLookup = await handler.HandleAsync(saveId, SearchAthletesQuery.Empty with { Search = activeName, Take = 10 }).ConfigureAwait(false);
        AthleteSearchResultDto active = activeLookup.Results.Single(e => e.AthleteId == activeId);
        active.NonPoolSeasons.ShouldBe(1);
        active.IsActive.ShouldBeTrue();

        SearchAthletesResponse poolLookup = await handler.HandleAsync(saveId, SearchAthletesQuery.Empty with { Search = poolName, Take = 10 }).ConfigureAwait(false);
        AthleteSearchResultDto pool = poolLookup.Results.Single(e => e.AthleteId == poolId);
        pool.NonPoolSeasons.ShouldBe(0);
        pool.IsActive.ShouldBeFalse();

        SearchAthletesResponse filtered = await handler.HandleAsync(saveId, new SearchAthletesQuery(
            null, 1, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, 2000)).ConfigureAwait(false);
        filtered.TotalCount.ShouldBe(768);
        filtered.Results.All(e => e.NonPoolSeasons >= 1).ShouldBeTrue();
    }

    [Fact]
    public async Task Search_TextSearch_ComposesWithFilters()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Search Text", 111UL, 222UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            await AssertTextSearchAsync(store, saveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertTextSearchAsync(SaveStore store, Guid saveId)
    {
        SearchAthletesHandler handler = new(store);
        string name;
        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            name = await context.SaveAthletes.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Name).FirstAsync().ConfigureAwait(false);
        }

        string partial = name[..Math.Min(6, name.Length)];
        string mixed = string.Concat(partial.Select(c => char.IsLower(c) ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)));

        SearchAthletesResponse found = await handler.HandleAsync(saveId, SearchAthletesQuery.Empty with { Search = mixed, Take = 500 }).ConfigureAwait(false);
        found.TotalCount.ShouldBeGreaterThan(0);
        found.Results.All(e => e.Name.Contains(partial, StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();

        SearchAthletesResponse impossible = await handler.HandleAsync(
            saveId, SearchAthletesQuery.Empty with { Search = "no-such-athlete-zzz", Take = 50 }).ConfigureAwait(false);
        impossible.TotalCount.ShouldBe(0);
        impossible.Results.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Search_AfterFullSeason_HonoursAndTitlesDistinguished()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Search Season", 777UL, 888UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            global::MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues.CompleteStageForAllLeaguesHandler bulk = new(store);
            for (int stage = 1; stage <= 32; stage++)
            {
                await bulk.HandleAsync(saveId);
            }

            await AssertSeasonHonoursAsync(store, saveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertSeasonHonoursAsync(SaveStore store, Guid saveId)
    {
        SearchAthletesHandler handler = new(store);
        SearchAthletesResponse champions = await handler.HandleAsync(
            saveId, SearchAthletesQuery.Empty with { MinTitles = 1, Take = 500 }).ConfigureAwait(false);
        champions.TotalCount.ShouldBe(24);

        SearchAthletesResponse honoured = await handler.HandleAsync(
            saveId, SearchAthletesQuery.Empty with { MinHonours = 1, Take = 500 }).ConfigureAwait(false);
        honoured.TotalCount.ShouldBe(72);

        SearchAthletesResponse podiumsOnly = await handler.HandleAsync(
            saveId, SearchAthletesQuery.Empty with { MinHonours = 1, HasTitle = "none", Take = 500 }).ConfigureAwait(false);
        podiumsOnly.TotalCount.ShouldBe(48);
        podiumsOnly.Results.All(e => e.TitlesCount == 0 && e.HonoursCount >= 1).ShouldBeTrue();

        SearchAthletesResponse best = await handler.HandleAsync(
            saveId, SearchAthletesQuery.Empty with { BestFinishMax = 1, Take = 500 }).ConfigureAwait(false);
        best.TotalCount.ShouldBe(24);
    }

    [Fact]
    public async Task Search_PoolReturn_RetainsNonPoolSeasons()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Search Pool Return", 707UL, 808UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            global::MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues.CompleteStageForAllLeaguesHandler bulk = new(store);
            await bulk.HandleAsync(saveId);

            int activeId;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                activeId = await SimulatePoolReturnAsync(context);
            }

            SearchAthletesHandler handler = new(store);
            await AssertPoolReturnRetainedAsync(store, handler, saveId, activeId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<int> SimulatePoolReturnAsync(SaveDbContext context)
    {
        int activeId = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.LeagueId != null)
            .OrderBy(e => e.SaveAthleteId)
            .Select(e => e.SaveAthleteId)
            .FirstAsync().ConfigureAwait(false);

        // Simulate a later season in which everyone returns to the pool:
        // historical S1 memberships persist, so earlier non-pool seasons
        // must still count.
        SeasonEntity season2 = new() { SeasonNumber = 2, HasSuperleague = false, IsComplete = false };
        context.Seasons.Add(season2);
        await context.SaveChangesAsync().ConfigureAwait(false);
        List<SeasonMembershipEntity> season1 = await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId != season2.Id)
            .ToListAsync().ConfigureAwait(false);
        foreach (SeasonMembershipEntity membership in season1)
        {
            context.SeasonMemberships.Add(new SeasonMembershipEntity
            {
                SeasonId = season2.Id,
                LeagueId = null,
                SaveAthleteId = membership.SaveAthleteId,
                SportingColor = membership.SportingColor,
                DrawIndex = membership.DrawIndex,
            });
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
        await global::MtgSoloSports.Features.Athletes.Projections.AthleteProjectionUpdater.RebuildAllAsync(
            context, global::MtgSoloSports.SimulationKernel.Rules.RulesV1.CreateDefault(), CancellationToken.None).ConfigureAwait(false);
        return activeId;
    }

    private static async Task AssertPoolReturnRetainedAsync(
        SaveStore store, SearchAthletesHandler handler, Guid saveId, int activeId)
    {
        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            string activeName = await context.SaveAthletes.AsNoTracking().Where(e => e.Id == activeId).Select(e => e.Name).SingleAsync().ConfigureAwait(false);
            SearchAthletesResponse lookup = await handler.HandleAsync(saveId, SearchAthletesQuery.Empty with { Search = activeName, Take = 10 }).ConfigureAwait(false);
            AthleteSearchResultDto returned = lookup.Results.Single(e => e.AthleteId == activeId);
            returned.NonPoolSeasons.ShouldBe(1);
            returned.IsActive.ShouldBeFalse();
        }

        SearchAthletesResponse poolNow = await handler.HandleAsync(
            saveId, SearchAthletesQuery.Empty with { CurrentLeagues = ["Common pool"], MinNonPoolSeasons = 1, Take = 2000 }).ConfigureAwait(false);
        poolNow.TotalCount.ShouldBe(768);
        poolNow.Results.All(e => !e.IsActive && e.NonPoolSeasons >= 1).ShouldBeTrue();
        poolNow.Results.Any(e => e.AthleteId == activeId).ShouldBeTrue();
    }

    private static SearchAthletesFilter.Candidate BuildCandidate(
        int id, string name, int color, bool active, string? league, int nonPool, IReadOnlyList<string> types, int honours, int titles, int best)
    {
        return new SearchAthletesFilter.Candidate(
            id, name, null, "Creature", color, "White", types, active, league, active ? 0 : null,
            nonPool, honours, titles, best == 0 ? null : best, best == 0 ? null : 1,
            false, 0, 0, 0, 0);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-search-" + Guid.NewGuid().ToString("N"));
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
