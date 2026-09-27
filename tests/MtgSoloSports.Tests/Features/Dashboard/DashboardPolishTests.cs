using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Leagues.CurrentStandings;
using MtgSoloSports.Features.Leagues.SeasonProgress;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.CompleteSeason;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Dashboard;

/// <summary>
/// MSS-032 polish: dashboard-supporting read models expose league kind for zone
/// display, standings expose sporting color plus artwork, and athlete profiles
/// surface honours, movements and Cup selections from normalized tables only.
/// </summary>
public sealed class DashboardPolishTests
{
    [Fact]
    public async Task SeasonProgress_ExposesFeederKind_ForZoneDisplay()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Progress Kind", 101UL, 202UL, UniverseTestCatalog.Build());
            GetSeasonProgressHandler progress = new(store);
            GetSeasonProgressResponse response = await progress.HandleAsync(created.Detail.SaveId, 1);
            response.Leagues.Count.ShouldBe(8);
            foreach (SeasonProgressLeague league in response.Leagues)
            {
                league.LeagueKind.ShouldBe("Feeder");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentStandings_ExposesSportingColorAndArtwork()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Standings Color", 303UL, 404UL, UniverseTestCatalog.Build());
            CompleteStageForAllLeaguesHandler bulk = new(store);
            await bulk.HandleAsync(created.Detail.SaveId);

            List<int> leagueIds = await AllLeagueIdsAsync(store, created.Detail.SaveId);
            GetCurrentStandingsHandler standings = new(store);
            GetCurrentStandingsResponse response = await standings.HandleAsync(created.Detail.SaveId, leagueIds[0]);
            response.Standings.Count.ShouldBe(32);

            Dictionary<int, (int Color, string? Image)> cards = await LoadCardsAsync(store, created.Detail.SaveId);
            foreach (CurrentStandingEntry entry in response.Standings)
            {
                cards.ShouldContainKey(entry.AthleteId);
                (int color, string? image) = cards[entry.AthleteId];
                entry.SportingColor.ShouldBe(color);
                entry.SportingColorName.ShouldBe(((SportingColor)color).ToString());
                entry.ImageUrl.ShouldBe(image);
            }

            // Composition sums to the full league size without applying quotas.
            response.Standings.GroupBy(e => e.SportingColorName, StringComparer.Ordinal).Sum(g => g.Count()).ShouldBe(32);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AthleteProfile_ExposesHonoursMovementsAndSelections()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Profile Polish", 505UL, 606UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            List<int> leagueIds = await AllLeagueIdsAsync(store, saveId);
            GetAthleteProfileHandler profiles = new(store);

            // Before any simulation the new lists are empty, not missing.
            GetCurrentStandingsHandler standings = new(store);
            CompleteStageForAllLeaguesHandler bulk = new(store);
            await bulk.HandleAsync(saveId);
            GetCurrentStandingsResponse provisional = await standings.HandleAsync(saveId, leagueIds[0]);
            int sampleAthlete = provisional.Standings[0].AthleteId;
            GetAthleteProfileResponse before = await profiles.HandleAsync(saveId, sampleAthlete);
            before.Honours.ShouldBeEmpty();
            before.Movements.ShouldBeEmpty();
            before.CupSelections.ShouldBeEmpty();

            // Complete Season 1: honours persist for the eight feeder champions.
            CompleteSeasonHandler completeSeason = new(store);
            await completeSeason.HandleAsync(saveId);
            List<HonourAthlete> honours = await LoadHonoursAsync(store, saveId);
            honours.Count.ShouldBe(8);
            int champion = honours[0].AthleteId;
            GetAthleteProfileResponse withHonour = await profiles.HandleAsync(saveId, champion);
            withHonour.Honours.Count.ShouldBeGreaterThanOrEqualTo(1);
            withHonour.Honours.Any(h => h.SeasonNumber == 1).ShouldBeTrue();

            // Inaugural movement then rebalancing are explicit Next Event steps.
            AdvanceToNextEventHandler advance = new(store);
            AdvanceToNextEventResponse inaugural = await advance.HandleAsync(saveId);
            inaugural.ExecutedAction.ShouldBe(SeasonLifecycleActions.ResolveInauguralMovement);
            GetAthleteProfileResponse withMovement = await profiles.HandleAsync(saveId, champion);
            withMovement.Movements.Any(m => string.Equals(m.Kind, "InauguralPromotion", StringComparison.Ordinal)).ShouldBeTrue();

            AdvanceToNextEventResponse rebalanced = await advance.HandleAsync(saveId);
            rebalanced.ExecutedAction.ShouldBe(SeasonLifecycleActions.RebalanceFeeders);

            // Color Cup selection persists 32 selections; a selected athlete surfaces them.
            AdvanceToNextEventResponse selected = await advance.HandleAsync(saveId);
            selected.ExecutedAction.ShouldBe(SeasonLifecycleActions.SelectColorCup);
            int selectedAthlete = await FirstSelectedAthleteAsync(store, saveId);
            GetAthleteProfileResponse withSelection = await profiles.HandleAsync(saveId, selectedAthlete);
            withSelection.CupSelections.Any(s => string.Equals(s.CupKind, "ColorCup", StringComparison.Ordinal)).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record HonourAthlete(int AthleteId, int SeasonNumber);

    private static async Task<List<HonourAthlete>> LoadHonoursAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Honours
            .AsNoTracking()
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.LeagueName)
            .Select(e => new HonourAthlete(e.SaveAthleteId, e.SeasonNumber))
            .ToListAsync()
            .ConfigureAwait(false);
    }

    private static async Task<int> FirstSelectedAthleteAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.ColorCupSelections
            .AsNoTracking()
            .OrderBy(e => e.SourceSeasonNumber)
            .ThenBy(e => e.SelectionRank)
            .Select(e => e.SaveAthleteId)
            .FirstAsync()
            .ConfigureAwait(false);
    }

    private static async Task<Dictionary<int, (int Color, string? Image)>> LoadCardsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => (e.SportingColor, e.ImageUrl))
            .ConfigureAwait(false);
    }

    private static async Task<List<int>> AllLeagueIdsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Leagues.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-polish-" + Guid.NewGuid().ToString("N"));
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
