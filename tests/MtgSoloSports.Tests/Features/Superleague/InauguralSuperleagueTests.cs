using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.GetInauguralRoster;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Superleague;

public sealed class InauguralSuperleagueTests
{
    [Fact]
    public async Task Create_BeforeSeasonComplete_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Inaugural Early", 111UL, 222UL, UniverseTestCatalog.Build());
            CreateInauguralSuperleagueHandler handler = new(store);
            await Should.ThrowAsync<CreateInauguralSuperleagueConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetRoster_BeforeTransition_ReturnsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Inaugural Missing", 333UL, 444UL, UniverseTestCatalog.Build());
            GetInauguralRosterHandler query = new(store);
            await Should.ThrowAsync<InauguralRosterNotFoundException>(
                () => query.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Create_AfterSeasonOne_SelectsTopFourPerLeague()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Inaugural Full", 707UL, 808UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);

            (int stageCount, int seasonCount, long lifetimeBonus, ulong rngState) = await CapturePreTransitionAsync(store, created.Detail.SaveId);

            CreateInauguralSuperleagueHandler handler = new(store);
            CreateInauguralSuperleagueResponse response = await handler.HandleAsync(created.Detail.SaveId);

            response.SeasonOneNumber.ShouldBe(1);
            response.SeasonTwoNumber.ShouldBe(2);
            response.Members.Count.ShouldBe(32);
            response.MovementCount.ShouldBe(32);
            response.SuperleagueLeagueName.ShouldBe("Superleague");
            response.FeederRetention.Count.ShouldBe(24);
            response.FeederRetention.Count(r => r.RetainedCount == 28).ShouldBe(8);
            response.FeederRetention.Count(r => r.RetainedCount == 32).ShouldBe(16);

            await AssertTransitionAsync(store, created.Detail.SaveId, response);

            // Bonus history preserved: no standing rows added or changed.
            (int stageAfter, int seasonAfter, long lifetimeAfter, ulong rngAfter) =
                await CapturePreTransitionAsync(store, created.Detail.SaveId);
            stageAfter.ShouldBe(stageCount);
            seasonAfter.ShouldBe(seasonCount);
            lifetimeAfter.ShouldBe(lifetimeBonus);
            rngAfter.ShouldBe(rngState);

            // Roster read matches the creation response and is stable.
            GetInauguralRosterHandler query = new(store);
            GetInauguralRosterResponse roster = await query.HandleAsync(created.Detail.SaveId);
            roster.Members.Count.ShouldBe(32);
            roster.MovementCount.ShouldBe(32);
            roster.Members.Select(m => m.AthleteId).ShouldBe(response.Members.Select(m => m.AthleteId).ToList());

            GetInauguralRosterResponse again = await query.HandleAsync(created.Detail.SaveId);
            again.Members.Select(m => m.AthleteId).ShouldBe(roster.Members.Select(m => m.AthleteId).ToList());

            // Second creation attempt conflicts.
            await Should.ThrowAsync<CreateInauguralSuperleagueConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Create_SameSeed_ProducesSameRoster()
    {
        // MSS-067: one Season 1 simulation forked into two isolated saves instead
        // of simulating the same season twice; both transitions still execute
        // independently from bit-identical starting state.
        var (firstStore, firstRoot) = CreateStore();
        try
        {
            SaveStore.CreationRecord first = await firstStore.CreateAsync("Inaugural A", 9001UL, 7002UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(firstStore, first.Detail.SaveId);
            var (secondStore, secondRoot, secondId) = await TestSaveStores.ForkAsync(firstStore, first.Detail.SaveId, "mtgsolosports-inaugural-");
            try
            {
                CreateInauguralSuperleagueHandler firstHandler = new(firstStore);
                CreateInauguralSuperleagueHandler secondHandler = new(secondStore);
                CreateInauguralSuperleagueResponse responseA = await firstHandler.HandleAsync(first.Detail.SaveId);
                CreateInauguralSuperleagueResponse responseB = await secondHandler.HandleAsync(secondId);

                responseA.Members.Select(m => m.Name).ShouldBe(responseB.Members.Select(m => m.Name).ToList());
                responseA.Members.Select(m => m.FromSeasonRank).ShouldBe(responseB.Members.Select(m => m.FromSeasonRank).ToList());
            }
            finally
            {
                TestSaveStores.DeleteRoot(secondRoot);
            }
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
        }
    }

    private sealed record TransitionState(
        SeasonEntity SeasonOne,
        SeasonEntity SeasonTwo,
        int CurrentSeason,
        List<LeagueEntity> LeaguesTwo,
        List<SeasonMembershipEntity> MembershipsOne,
        List<SeasonMembershipEntity> MembershipsTwo);

    private static async Task AssertTransitionAsync(
        SaveStore store,
        Guid saveId,
        CreateInauguralSuperleagueResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        TransitionState state = await LoadTransitionStateAsync(context).ConfigureAwait(false);
        AssertSeasons(state);
        List<int> superIds = AssertSuperleagueSelection(state, response);
        await AssertProvenanceAsync(context, state, response).ConfigureAwait(false);
        AssertPoolUntouched(state);
        AssertFeederRetention(state, response);
        await AssertMovementsAndProjectionsAsync(context, state, response, superIds).ConfigureAwait(false);
    }

    private static async Task<TransitionState> LoadTransitionStateAsync(SaveDbContext context)
    {
        SeasonEntity seasonOne = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        SeasonEntity seasonTwo = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<LeagueEntity> leaguesTwo = await context.Leagues.AsNoTracking().Where(e => e.SeasonId == seasonTwo.Id).ToListAsync().ConfigureAwait(false);
        List<SeasonMembershipEntity> membershipsOne = await context.SeasonMemberships.AsNoTracking().Where(e => e.SeasonId == seasonOne.Id).ToListAsync().ConfigureAwait(false);
        List<SeasonMembershipEntity> membershipsTwo = await context.SeasonMemberships.AsNoTracking().Where(e => e.SeasonId == seasonTwo.Id).ToListAsync().ConfigureAwait(false);
        SaveMetadataEntity metadata = await context.SaveMetadata.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return new TransitionState(seasonOne, seasonTwo, metadata.CurrentSeason, leaguesTwo, membershipsOne, membershipsTwo);
    }

    private static void AssertSeasons(TransitionState state)
    {
        state.SeasonOne.IsComplete.ShouldBeTrue();
        state.SeasonTwo.HasSuperleague.ShouldBeTrue();
        state.SeasonTwo.IsComplete.ShouldBeFalse();
        state.CurrentSeason.ShouldBe(1);
        state.LeaguesTwo.Count.ShouldBe(25);
        state.LeaguesTwo.Count(l => l.Kind == (int)LeagueKind.Superleague).ShouldBe(1);
        state.LeaguesTwo.Count(l => l.Kind == (int)LeagueKind.Feeder).ShouldBe(24);
        state.MembershipsOne.Count.ShouldBe(2048);
        state.MembershipsTwo.Count.ShouldBe(2048);
    }

    private static List<int> AssertSuperleagueSelection(TransitionState state, CreateInauguralSuperleagueResponse response)
    {
        List<int> superIds = state.MembershipsTwo.Where(m => m.LeagueId == response.SuperleagueLeagueId).Select(m => m.SaveAthleteId).ToList();
        superIds.Count.ShouldBe(32);
        superIds.Distinct().Count().ShouldBe(32);
        superIds.ToHashSet().SetEquals(response.Members.Select(m => m.AthleteId).ToHashSet()).ShouldBeTrue();
        HashSet<int> activeTwo = state.MembershipsTwo.Where(m => m.LeagueId is not null).Select(m => m.SaveAthleteId).ToHashSet();
        activeTwo.Count.ShouldBe(32 + (8 * 28) + (16 * 32));
        foreach (var group in response.Members.GroupBy(m => m.FromLeagueId))
        {
            group.Count().ShouldBe(4);
            group.Select(m => m.FromSeasonRank).OrderBy(r => r).ShouldBe([1, 2, 3, 4]);
        }

        return superIds;
    }

    private static async Task AssertProvenanceAsync(
        SaveDbContext context,
        TransitionState state,
        CreateInauguralSuperleagueResponse response)
    {
        Dictionary<(int LeagueId, int AthleteId), int> ranks = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == state.SeasonOne.Id)
            .ToDictionaryAsync(e => (e.LeagueId, e.SaveAthleteId), e => e.SeasonRank)
            .ConfigureAwait(false);
        Dictionary<int, int> leagueByAthleteOne = state.MembershipsOne
            .Where(m => m.LeagueId is not null)
            .ToDictionary(m => m.SaveAthleteId, m => m.LeagueId!.Value);
        foreach (InauguralSuperleagueMember member in response.Members)
        {
            member.FromSeasonRank.ShouldBeInRange(1, 4);
            leagueByAthleteOne[member.AthleteId].ShouldBe(member.FromLeagueId);
            ranks[(member.FromLeagueId, member.AthleteId)].ShouldBe(member.FromSeasonRank);
        }
    }

    private static void AssertPoolUntouched(TransitionState state)
    {
        HashSet<int> poolOne = state.MembershipsOne.Where(m => m.LeagueId is null).Select(m => m.SaveAthleteId).ToHashSet();
        HashSet<int> poolTwo = state.MembershipsTwo.Where(m => m.LeagueId is null).Select(m => m.SaveAthleteId).ToHashSet();
        poolOne.SetEquals(poolTwo).ShouldBeTrue();
        poolTwo.Count.ShouldBe(2048 - 768);
    }

    private static void AssertFeederRetention(TransitionState state, CreateInauguralSuperleagueResponse response)
    {
        Dictionary<int, int> colorByAthleteOne = state.MembershipsOne.ToDictionary(m => m.SaveAthleteId, m => m.SportingColor);
        Dictionary<(int Color, int Division), LeagueEntity> feederTwoByColorDivision = state.LeaguesTwo
            .Where(l => l.Kind == (int)LeagueKind.Feeder)
            .ToDictionary(l => (l.SportingColor, l.FeederDivision));
        Dictionary<int, SeasonMembershipEntity> oneByAthlete = state.MembershipsOne.ToDictionary(m => m.SaveAthleteId);
        foreach (SeasonMembershipEntity membership in state.MembershipsTwo)
        {
            if (membership.LeagueId is null || membership.LeagueId == response.SuperleagueLeagueId)
            {
                continue;
            }

            state.MembershipsOne.Single(m => m.SaveAthleteId == membership.SaveAthleteId).LeagueId.ShouldNotBeNull();
            colorByAthleteOne[membership.SaveAthleteId].ShouldBe(membership.SportingColor);
            SeasonMembershipEntity source = oneByAthlete[membership.SaveAthleteId];
            int sourceDivision = source.DrawIndex < 32 ? 1 : source.DrawIndex < 64 ? 2 : 3;
            feederTwoByColorDivision[(membership.SportingColor, sourceDivision)].Id.ShouldBe(membership.LeagueId.Value);
        }
    }

    private static async Task AssertMovementsAndProjectionsAsync(
        SaveDbContext context,
        TransitionState state,
        CreateInauguralSuperleagueResponse response,
        List<int> superIds)
    {
        List<MovementEntity> movements = await context.Movements.AsNoTracking().Where(e => e.ToSeasonId == state.SeasonTwo.Id).ToListAsync().ConfigureAwait(false);
        movements.Count.ShouldBe(32);
        movements.All(m => m.Kind == (int)MovementKind.InauguralPromotion).ShouldBeTrue();
        movements.All(m => m.FromSeasonId == state.SeasonOne.Id && m.ToLeagueId == response.SuperleagueLeagueId).ShouldBeTrue();
        movements.Select(m => m.SaveAthleteId).ToHashSet().SetEquals(superIds.ToHashSet()).ShouldBeTrue();
        foreach (var group in movements.GroupBy(m => m.FromLeagueId))
        {
            group.Count().ShouldBe(4);
        }

        InauguralSuperleagueMember sample = response.Members[0];
        AthleteCareerEntity career = await context.AthleteCareers.AsNoTracking().SingleAsync(e => e.SaveAthleteId == sample.AthleteId).ConfigureAwait(false);
        career.CurrentLeagueName.ShouldBe("Superleague");
        career.IsActive.ShouldBeTrue();
    }

    private static async Task<(int StageCount, int SeasonCount, long LifetimeBonus, ulong RngState)> CapturePreTransitionAsync(
        SaveStore store,
        Guid saveId)
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

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-inaugural-" + Guid.NewGuid().ToString("N"));
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
