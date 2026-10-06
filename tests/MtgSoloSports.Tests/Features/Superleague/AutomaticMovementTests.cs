using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.GetAutomaticMovement;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Superleague;

public sealed class AutomaticMovementTests
{
    [Fact]
    public async Task Resolve_BeforeSuperleagueSeasonComplete_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Auto Early", 111UL, 222UL, UniverseTestCatalog.Build());
            ResolveAutomaticMovementHandler handler = new(store);
            await Should.ThrowAsync<ResolveAutomaticMovementConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Get_BeforeResolved_ReturnsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Auto Missing", 333UL, 444UL, UniverseTestCatalog.Build());
            GetAutomaticMovementHandler query = new(store);
            await Should.ThrowAsync<AutomaticMovementNotFoundException>(
                () => query.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resolve_AfterSyntheticSeasonTwo_IdentifiesBandsAndPreservesHistory()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Auto Full", 707UL, 808UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);

            await FillSeasonTwoFeedersAsync(store, created.Detail.SaveId);
            await InsertSyntheticSeasonTwoStandingsAsync(store, created.Detail.SaveId, placeWhiteLast: false);

            (int stageCount, int seasonCount, long lifetimeBonus, ulong rngState) =
                await CapturePreservationAsync(store, created.Detail.SaveId);

            ResolveAutomaticMovementHandler handler = new(store);
            ResolveAutomaticMovementResponse response = await handler.HandleAsync(created.Detail.SaveId);

            AssertBands(response);
            await AssertTransitionAsync(store, created.Detail.SaveId, response);
            await AssertPreservationAsync(store, created.Detail.SaveId, stageCount, seasonCount, lifetimeBonus, rngState);
            await AssertQueryMatchesAsync(store, created.Detail.SaveId, response);

            await Should.ThrowAsync<ResolveAutomaticMovementConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resolve_MultipleRelegatedSameColor_ShareReturningFeeder()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Auto Cluster", 9001UL, 7002UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);

            await FillSeasonTwoFeedersAsync(store, created.Detail.SaveId);
            // Force all White Superleague athletes into the relegated band so the
            // White feeder receives several returning athletes at once.
            await InsertSyntheticSeasonTwoStandingsAsync(store, created.Detail.SaveId, placeWhiteLast: true);

            ResolveAutomaticMovementHandler handler = new(store);
            ResolveAutomaticMovementResponse response = await handler.HandleAsync(created.Detail.SaveId);

            await AssertWhiteClusterAsync(store, created.Detail.SaveId, response);
            await AssertTransitionAsync(store, created.Detail.SaveId, response);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resolve_ResponseCarriesCardArtwork_MatchesPersistedAthletes()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Auto Artwork", 4242UL, 8484UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);

            await FillSeasonTwoFeedersAsync(store, created.Detail.SaveId);
            await InsertSyntheticSeasonTwoStandingsAsync(store, created.Detail.SaveId, placeWhiteLast: false);

            ResolveAutomaticMovementHandler handler = new(store);
            ResolveAutomaticMovementResponse response = await handler.HandleAsync(created.Detail.SaveId);

            // Presentation-only artwork must ride along with authoritative movement
            // facts; sporting counts stay exactly 8 promotions / 8 relegations.
            response.Promoted.Count.ShouldBe(8);
            response.Relegated.Count.ShouldBe(8);

            Dictionary<int, string?> images = await LoadAthleteImagesAsync(store, created.Detail.SaveId);
            AssertImagesMatch(response.Safe, images);
            AssertImagesMatch(response.Promoted, images);
            AssertImagesMatch(response.Relegated, images);
            AssertImagesMatch(response.QualifierIncumbents, images);
            AssertImagesMatch(response.QualifierChallengers, images);

            // The historical read model carries the same artwork so revisiting a
            // completed event renders the same tiles without rederiving leagues.
            GetAutomaticMovementHandler query = new(store);
            GetAutomaticMovementResponse historical = await query.HandleAsync(created.Detail.SaveId, fromSeasonNumber: 2);
            AssertImagesMatch(historical.Promoted, images);
            AssertImagesMatch(historical.Relegated, images);
            historical.Promoted.Select(m => m.AthleteId).ShouldBe(response.Promoted.Select(m => m.AthleteId).ToList());
            historical.Relegated.Select(m => m.AthleteId).ShouldBe(response.Relegated.Select(m => m.AthleteId).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertImagesMatch(
        IReadOnlyList<AutomaticMovementMember> members,
        Dictionary<int, string?> images)
    {
        foreach (AutomaticMovementMember member in members)
        {
            images.TryGetValue(member.AthleteId, out string? expected);
            member.ImageUrl.ShouldBe(expected);
        }
    }

    private static async Task<Dictionary<int, string?>> LoadAthleteImagesAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.ImageUrl).ConfigureAwait(false);
    }

    private static void AssertBands(ResolveAutomaticMovementResponse response)
    {
        response.FromSeasonNumber.ShouldBe(2);
        response.ToSeasonNumber.ShouldBe(3);
        response.Safe.Count.ShouldBe(16);
        response.Promoted.Count.ShouldBe(8);
        response.Relegated.Count.ShouldBe(8);
        response.QualifierIncumbents.Count.ShouldBe(8);
        response.QualifierChallengers.Count.ShouldBe(24);
        response.MovementCount.ShouldBe(48);
        response.SuperleagueLeagueName.ShouldBe("Superleague");
        response.Safe.Select(m => m.FromSeasonRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, 16).ToList());
        response.QualifierIncumbents.Select(m => m.FromSeasonRank).OrderBy(r => r).ShouldBe(Enumerable.Range(17, 8).ToList());
        response.Relegated.Select(m => m.FromSeasonRank).OrderBy(r => r).ShouldBe(Enumerable.Range(25, 8).ToList());
        response.Promoted.Select(m => m.FromSeasonRank).ShouldAllBe(r => r == 1);
        foreach (var group in response.QualifierChallengers.GroupBy(m => m.FromLeagueId))
        {
            group.Count().ShouldBe(3);
        }
    }

    private static async Task AssertPreservationAsync(
        SaveStore store,
        Guid saveId,
        int stageCount,
        int seasonCount,
        long lifetimeBonus,
        ulong rngState)
    {
        (int stageAfter, int seasonAfter, long lifetimeAfter, ulong rngAfter) =
            await CapturePreservationAsync(store, saveId).ConfigureAwait(false);
        stageAfter.ShouldBe(stageCount);
        seasonAfter.ShouldBe(seasonCount);
        lifetimeAfter.ShouldBe(lifetimeBonus);
        rngAfter.ShouldBe(rngState);
    }

    private static async Task AssertQueryMatchesAsync(
        SaveStore store,
        Guid saveId,
        ResolveAutomaticMovementResponse response)
    {
        GetAutomaticMovementHandler query = new(store);
        GetAutomaticMovementResponse summary = await query.HandleAsync(saveId).ConfigureAwait(false);
        summary.FromSeasonNumber.ShouldBe(2);
        summary.ToSeasonNumber.ShouldBe(3);
        summary.MovementCount.ShouldBe(48);
        summary.Safe.Select(m => m.AthleteId).ShouldBe(response.Safe.Select(m => m.AthleteId).ToList());
        summary.Promoted.Select(m => m.AthleteId).ShouldBe(response.Promoted.Select(m => m.AthleteId).ToList());
        summary.Relegated.Select(m => m.AthleteId).ShouldBe(response.Relegated.Select(m => m.AthleteId).ToList());
        GetAutomaticMovementResponse again = await query.HandleAsync(saveId, fromSeasonNumber: 2).ConfigureAwait(false);
        again.MovementCount.ShouldBe(48);
    }

    private static async Task AssertWhiteClusterAsync(
        SaveStore store,
        Guid saveId,
        ResolveAutomaticMovementResponse response)
    {
        response.Relegated.Count.ShouldBe(8);
        List<AutomaticMovementMember> whiteRelegated = response.Relegated
            .Where(m => string.Equals(m.SportingColor, SportingColor.White.ToString(), StringComparison.Ordinal))
            .ToList();
        whiteRelegated.Count.ShouldBeGreaterThanOrEqualTo(3);
        (int whiteCount, int superCount) = await LoadWhiteAndSuperCountsAsync(store, saveId, response.SuperleagueLeagueId).ConfigureAwait(false);
        whiteCount.ShouldBe(31 + whiteRelegated.Count);
        superCount.ShouldBe(32);
    }

    private static async Task<(int WhiteCount, int SuperCount)> LoadWhiteAndSuperCountsAsync(
        SaveStore store,
        Guid saveId,
        int superleagueId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonThree = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);
        List<LeagueEntity> feedersThree = await context.Leagues.AsNoTracking()
            .Where(e => e.SeasonId == seasonThree.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync().ConfigureAwait(false);
        LeagueEntity whiteFeeder = feedersThree.Single(l => l.SportingColor == (int)SportingColor.White);
        int whiteCount = await context.SeasonMemberships
            .CountAsync(e => e.SeasonId == seasonThree.Id && e.LeagueId == whiteFeeder.Id).ConfigureAwait(false);
        int superCount = await context.SeasonMemberships
            .CountAsync(e => e.SeasonId == seasonThree.Id && e.LeagueId == superleagueId).ConfigureAwait(false);
        return (whiteCount, superCount);
    }

    private static async Task AssertTransitionAsync(
        SaveStore store,
        Guid saveId,
        ResolveAutomaticMovementResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.FromSeasonNumber).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.ToSeasonNumber).ConfigureAwait(false);
        next.HasSuperleague.ShouldBeTrue();
        next.IsComplete.ShouldBeFalse();

        List<LeagueEntity> leaguesNext = await context.Leagues.AsNoTracking().Where(e => e.SeasonId == next.Id).ToListAsync().ConfigureAwait(false);
        leaguesNext.Count.ShouldBe(25);
        leaguesNext.Count(l => l.Kind == (int)LeagueKind.Superleague).ShouldBe(1);
        leaguesNext.Count(l => l.Kind == (int)LeagueKind.Feeder).ShouldBe(24);

        List<SeasonMembershipEntity> membershipsSource = await context.SeasonMemberships.AsNoTracking().Where(e => e.SeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        List<SeasonMembershipEntity> membershipsNext = await context.SeasonMemberships.AsNoTracking().Where(e => e.SeasonId == next.Id).ToListAsync().ConfigureAwait(false);
        membershipsSource.Count.ShouldBe(2048);
        membershipsNext.Count.ShouldBe(2048);

        HashSet<int> poolSource = membershipsSource.Where(m => m.LeagueId is null).Select(m => m.SaveAthleteId).ToHashSet();
        HashSet<int> poolNext = membershipsNext.Where(m => m.LeagueId is null).Select(m => m.SaveAthleteId).ToHashSet();
        poolSource.SetEquals(poolNext).ShouldBeTrue();

        Dictionary<int, int> colorByAthleteSource = membershipsSource.ToDictionary(m => m.SaveAthleteId, m => m.SportingColor);
        foreach (SeasonMembershipEntity membership in membershipsNext)
        {
            colorByAthleteSource[membership.SaveAthleteId].ShouldBe(membership.SportingColor);
        }

        List<MovementEntity> movements = await context.Movements.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id).ToListAsync().ConfigureAwait(false);
        movements.Count.ShouldBe(48);
        movements.Count(m => m.Kind == (int)MovementKind.AutomaticPromotion).ShouldBe(8);
        movements.Count(m => m.Kind == (int)MovementKind.AutomaticRelegation).ShouldBe(8);
        movements.Count(m => m.Kind == (int)MovementKind.QualifierIncumbent).ShouldBe(8);
        movements.Count(m => m.Kind == (int)MovementKind.QualifierChallenger).ShouldBe(8 * 3);
        movements.Select(m => m.SaveAthleteId).Distinct().Count().ShouldBe(48);

        HashSet<int> promotedIds = response.Promoted.Select(m => m.AthleteId).ToHashSet();
        HashSet<int> relegatedIds = response.Relegated.Select(m => m.AthleteId).ToHashSet();
        movements.Where(m => m.Kind == (int)MovementKind.AutomaticPromotion).Select(m => m.SaveAthleteId).ToHashSet().SetEquals(promotedIds).ShouldBeTrue();
        movements.Where(m => m.Kind == (int)MovementKind.AutomaticRelegation).Select(m => m.SaveAthleteId).ToHashSet().SetEquals(relegatedIds).ShouldBeTrue();

        int superNext = membershipsNext.Count(m => m.LeagueId == response.SuperleagueLeagueId);
        superNext.ShouldBe(32);

        SaveMetadataEntity metadata = await context.SaveMetadata.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        metadata.CurrentSeason.ShouldBe(1);
    }

    private static async Task<(int StageCount, int SeasonCount, long LifetimeBonus, ulong RngState)> CapturePreservationAsync(
        SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int stages = await context.StageStandings.CountAsync().ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync().ConfigureAwait(false);
        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths).ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return (stages, seasons, lifetime, (ulong)rng.State);
        }
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

    private static async Task FillSeasonTwoFeedersAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<LeagueEntity> feeders = await context.Leagues
            .Where(e => e.SeasonId == seasonTwo.Id && e.Kind == (int)LeagueKind.Feeder && e.FeederDivision == (int)FeederDivision.First)
            .ToListAsync().ConfigureAwait(false);
        feeders.Count.ShouldBe(8);
        foreach (LeagueEntity feeder in feeders.OrderBy(l => l.SportingColor))
        {
            int memberCount = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == seasonTwo.Id && e.LeagueId == feeder.Id).ConfigureAwait(false);
            memberCount.ShouldBe(28);
            List<SeasonMembershipEntity> poolForColor = await context.SeasonMemberships
                .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == null && e.SportingColor == feeder.SportingColor)
                .OrderBy(e => e.SaveAthleteId)
                .Take(4)
                .ToListAsync().ConfigureAwait(false);
            poolForColor.Count.ShouldBe(4);
            foreach (SeasonMembershipEntity pool in poolForColor)
            {
                pool.LeagueId = feeder.Id;
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);

        foreach (LeagueEntity feeder in feeders)
        {
            int memberCount = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == seasonTwo.Id && e.LeagueId == feeder.Id).ConfigureAwait(false);
            memberCount.ShouldBe(32);
        }
    }

    private static async Task InsertSyntheticSeasonTwoStandingsAsync(SaveStore store, Guid saveId, bool placeWhiteLast)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == seasonTwo.Id)
            .OrderBy(e => e.Id)
            .ToListAsync().ConfigureAwait(false);
        leagues.Count.ShouldBe(25);

        foreach (LeagueEntity league in leagues)
        {
            List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
                .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == league.Id)
                .ToListAsync().ConfigureAwait(false);
            memberships.Count.ShouldBe(32);

            List<SeasonMembershipEntity> ordered;
            if (league.Kind == (int)LeagueKind.Superleague && placeWhiteLast)
            {
                ordered = memberships
                    .OrderBy(m => m.SportingColor == (int)SportingColor.White ? 1 : 0)
                    .ThenBy(m => m.SaveAthleteId)
                    .ToList();
            }
            else
            {
                ordered = memberships.OrderBy(m => m.SaveAthleteId).ToList();
            }

            for (int i = 0; i < ordered.Count; i++)
            {
                int rank = i + 1;
                context.SeasonStandings.Add(new SeasonStandingEntity
                {
                    SeasonId = seasonTwo.Id,
                    LeagueId = league.Id,
                    SaveAthleteId = ordered[i].SaveAthleteId,
                    SeasonRank = rank,
                    TotalChampionshipPointsThousandths = 0,
                    TotalStageScoreThousandths = 0,
                    TotalBaseScoreThousandths = 0,
                    StageWins = 0,
                    RoundWins = 0,
                    StagePlaceCountsJson = "[]",
                    RoundPlaceCountsJson = "[]",
                    IsChampion = rank == 1,
                });
            }
        }

        seasonTwo.IsComplete = true;
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-auto-" + Guid.NewGuid().ToString("N"));
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
