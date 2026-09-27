using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.GetColorCupSelection;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class ColorCupSelectionApiTests
{
    [Fact]
    public async Task Select_Season1_PersistsEightTeamsOfFour_OrderedWithComponents()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Season1", 5150UL, 6161UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);

            SelectColorCupTeamsHandler handler = new(store);
            SelectColorCupTeamsResponse response = await handler.HandleAsync(created.Detail.SaveId);

            response.SourceSeasonNumber.ShouldBe(1);
            response.TotalSelected.ShouldBe(32);
            response.Teams.Count.ShouldBe(8);
            foreach (ColorCupTeamResult team in response.Teams)
            {
                team.Members.Count.ShouldBe(4);
                team.Members.Select(m => m.SelectionRank).OrderBy(r => r).ShouldBe([1, 2, 3, 4]);
                for (int i = 1; i < team.Members.Count; i++)
                {
                    (team.Members[i].FinalRatingThousandths <= team.Members[i - 1].FinalRatingThousandths).ShouldBeTrue();
                }

                foreach (ColorCupTeamMember member in team.Members)
                {
                    member.SportingColor.ShouldBe(team.SportingColorName);
                    member.BonusNormThousandths.ShouldBeInRange(0, 1000);
                    member.PerformanceNormThousandths.ShouldBeInRange(0, 1000);
                    member.FormNormThousandths.ShouldBeInRange(0, 1000);
                    member.PrestigeNormThousandths.ShouldBeInRange(0, 1000);
                    int expected = SelectionScore.Combine(
                        member.BonusNormThousandths,
                        member.PerformanceNormThousandths,
                        member.FormNormThousandths,
                        member.PrestigeNormThousandths,
                        response.CupBonusWeightPermille,
                        response.CupPerformanceWeightPermille,
                        response.CupFormWeightPermille,
                        response.CupPrestigeWeightPermille).Thousandths;
                    member.FinalRatingThousandths.ShouldBe(expected);
                }
            }

            await AssertPersistedMatchesResponseAsync(store, created.Detail.SaveId, response);
            await AssertQueryMatchesAsync(store, created.Detail.SaveId, response);

            await Should.ThrowAsync<SelectColorCupTeamsConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Select_SuperleagueAthletes_RepresentOriginalColor()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Super", 7171UL, 8181UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);
            RebalanceFeedersHandler rebalance = new(store);
            await rebalance.HandleAsync(created.Detail.SaveId);

            SelectColorCupTeamsHandler handler = new(store);
            SelectColorCupTeamsResponse response = await handler.HandleAsync(created.Detail.SaveId, sourceSeasonNumber: 1);

            response.TotalSelected.ShouldBe(32);
            HashSet<int> selectedIds = response.Teams.SelectMany(t => t.Members).Select(m => m.SaveAthleteId).ToHashSet();
            selectedIds.Count.ShouldBe(32);

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            SeasonEntity seasonTwo = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            LeagueEntity superleague = await context.Leagues.AsNoTracking()
                .SingleAsync(e => e.SeasonId == seasonTwo.Id && e.Kind == (int)LeagueKind.Superleague);
            List<int> superList = await context.SeasonMemberships
                .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == superleague.Id)
                .Select(e => e.SaveAthleteId)
                .ToListAsync();
            HashSet<int> superIds = superList.ToHashSet();
            Dictionary<int, int> colorByAthlete = await context.SaveAthletes
                .AsNoTracking()
                .ToDictionaryAsync(e => e.Id, e => e.SportingColor);

            // Every selected athlete must sit in its original-color team.
            foreach (ColorCupTeamResult team in response.Teams)
            {
                foreach (ColorCupTeamMember member in team.Members)
                {
                    colorByAthlete[member.SaveAthleteId].ShouldBe(team.SportingColor);
                }
            }

            // At least one Superleague athlete is selected (field is 32 of 2048;
            // with 32 superleague members the chance of zero overlap is negligible,
            // and the deterministic seed used here produces overlap).
            selectedIds.Intersect(superIds).Count().ShouldBeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Select_BeforeSeasonComplete_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Early", 9191UL, 202UL, UniverseTestCatalog.Build());
            SelectColorCupTeamsHandler handler = new(store);
            await Should.ThrowAsync<SelectColorCupTeamsConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Select_EvenSeason_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Even", 303UL, 404UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            await MarkSeasonTwoCompleteAsync(store, created.Detail.SaveId);

            SelectColorCupTeamsHandler handler = new(store);
            await Should.ThrowAsync<SelectColorCupTeamsConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId, sourceSeasonNumber: 2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Get_BeforeSelected_ReturnsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Missing", 505UL, 606UL, UniverseTestCatalog.Build());
            GetColorCupSelectionHandler query = new(store);
            await Should.ThrowAsync<ColorCupSelectionNotFoundException>(
                () => query.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertPersistedMatchesResponseAsync(
        SaveStore store,
        Guid saveId,
        SelectColorCupTeamsResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking()
            .SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        List<ColorCupSelectionEntity> rows = await context.ColorCupSelections
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .ToListAsync().ConfigureAwait(false);
        rows.Count.ShouldBe(32);
        foreach (ColorCupTeamResult team in response.Teams)
        {
            List<ColorCupSelectionEntity> colorRows = rows.Where(r => r.SportingColor == team.SportingColor).ToList();
            colorRows.Count.ShouldBe(4);
            foreach (ColorCupTeamMember member in team.Members)
            {
                ColorCupSelectionEntity row = colorRows.Single(r => r.SaveAthleteId == member.SaveAthleteId);
                row.SelectionRank.ShouldBe(member.SelectionRank);
                row.FinalRatingThousandths.ShouldBe(member.FinalRatingThousandths);
                row.BonusNormThousandths.ShouldBe(member.BonusNormThousandths);
                row.PerformanceNormThousandths.ShouldBe(member.PerformanceNormThousandths);
                row.FormNormThousandths.ShouldBe(member.FormNormThousandths);
                row.PrestigeNormThousandths.ShouldBe(member.PrestigeNormThousandths);
            }
        }
    }

    private static async Task AssertQueryMatchesAsync(
        SaveStore store,
        Guid saveId,
        SelectColorCupTeamsResponse response)
    {
        GetColorCupSelectionHandler query = new(store);
        GetColorCupSelectionResponse summary = await query.HandleAsync(saveId).ConfigureAwait(false);
        summary.SourceSeasonNumber.ShouldBe(response.SourceSeasonNumber);
        summary.TotalSelected.ShouldBe(response.TotalSelected);
        summary.Teams.Count.ShouldBe(8);
        foreach (GetColorCupTeam team in summary.Teams)
        {
            ColorCupTeamResult expected = response.Teams.Single(t => t.SportingColor == team.SportingColor);
            team.Members.Count.ShouldBe(4);
            foreach (GetColorCupMember member in team.Members)
            {
                ColorCupTeamMember match = expected.Members.Single(m => m.SaveAthleteId == member.SaveAthleteId);
                member.SelectionRank.ShouldBe(match.SelectionRank);
                member.FinalRatingThousandths.ShouldBe(match.FinalRatingThousandths);
            }
        }

        GetColorCupSelectionResponse again = await query.HandleAsync(saveId, sourceSeasonNumber: response.SourceSeasonNumber).ConfigureAwait(false);
        again.TotalSelected.ShouldBe(response.TotalSelected);
    }

    private static async Task CompleteSeasonOneAsync(SaveStore store, Guid saveId)
    {
        CompleteStageForAllLeaguesHandler bulk = new(store);
        for (int stage = 1; stage <= 32; stage++)
        {
            CompleteStageForAllLeaguesResponse completed = await bulk.HandleAsync(saveId).ConfigureAwait(false);
            completed.CompletedStage.ShouldBe(stage);
        }
    }

    private static async Task MarkSeasonTwoCompleteAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonOne = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        SeasonEntity seasonTwo = new() { SeasonNumber = 2, HasSuperleague = true, IsComplete = true };
        context.Seasons.Add(seasonTwo);
        await context.SaveChangesAsync().ConfigureAwait(false);
        _ = seasonOne;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-cup-" + Guid.NewGuid().ToString("N"));
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
