using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Simulation.CompleteSeason;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Stories;
using MtgSoloSports.Features.Stories.ListAthleteStories;
using MtgSoloSports.Features.Stories.ListRecent;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Stories;

public sealed class StoryEventsTests
{
    [Fact]
    public void Renderer_RendersAllKnownTypes_Deterministically()
    {
        StoryEventPayload stage = new("Alpha Athlete", 1, "White League", 3, StageRank: 1);
        string first = StoryEventRenderer.Render(StoryEventType.FirstStageWin, StoryEventRenderer.ToJson(stage));
        string second = StoryEventRenderer.Render(StoryEventType.FirstStageWin, StoryEventRenderer.ToJson(stage));
        first.ShouldBe(second);
        first.ShouldContain("Alpha Athlete");
        first.ShouldContain("White League");
        first.ShouldContain("Season 1");

        StoryEventPayload title = new("Beta Athlete", 2, "Blue League", SeasonRank: 1, TitleCount: 1);
        StoryEventRenderer.Render(StoryEventType.FirstLeagueTitle, StoryEventRenderer.ToJson(title))
            .ShouldContain("first Blue League championship");

        StoryEventPayload milestone = new("Beta Athlete", 3, "Blue League", SeasonRank: 1, TitleCount: 5);
        StoryEventRenderer.Render(StoryEventType.LeagueTitleMilestone, StoryEventRenderer.ToJson(milestone))
            .ShouldContain("title number 5");

        StoryEventPayload debut = new("Gamma Athlete", 2, ToLeagueName: "Superleague", FromSeasonNumber: 1, ToSeasonNumber: 2);
        StoryEventRenderer.Render(StoryEventType.FirstSuperleagueAppearance, StoryEventRenderer.ToJson(debut))
            .ShouldContain("Superleague for the first time");

        StoryEventPayload promotion = new(
            "Delta Athlete", 2, FromLeagueName: "Red League", ToLeagueName: "Superleague",
            FromSeasonNumber: 1, ToSeasonNumber: 2, FromSeasonRank: 1);
        StoryEventRenderer.Render(StoryEventType.Promotion, StoryEventRenderer.ToJson(promotion))
            .ShouldContain("promotion from Red League to Superleague");

        StoryEventPayload qualifierPromotion = promotion with { ViaQualifier = true };
        StoryEventRenderer.Render(StoryEventType.Promotion, StoryEventRenderer.ToJson(qualifierPromotion))
            .ShouldContain("via the qualifier");

        StoryEventPayload relegation = new(
            "Epsilon Athlete", 3, FromLeagueName: "Superleague", ToLeagueName: "Black League",
            FromSeasonNumber: 2, ToSeasonNumber: 3, FromSeasonRank: 30);
        StoryEventRenderer.Render(StoryEventType.Relegation, StoryEventRenderer.ToJson(relegation))
            .ShouldContain("relegated from Superleague to Black League");

        StoryEventPayload comeback = new(
            "Zeta Athlete", 2, ToLeagueName: "Green League",
            FromSeasonNumber: 1, ToSeasonNumber: 2, SportingColor: "Green");
        StoryEventRenderer.Render(StoryEventType.ReturnFromPool, StoryEventRenderer.ToJson(comeback))
            .ShouldContain("returns from the common pool to Green League");

        foreach (string known in StoryEventType.KnownTypes)
        {
            StoryEventType.IsKnown(known).ShouldBeTrue();
        }

        StoryEventRenderer.Render("cup_future_type", StoryEventRenderer.ToJson(stage))
            .ShouldContain("Season 1");
    }

    [Fact]
    public void TitleMilestone_Predicate_MarksNotableCountsOnly()
    {
        StoryEventRenderer.IsTitleMilestone(1).ShouldBeFalse();
        StoryEventRenderer.IsTitleMilestone(2).ShouldBeTrue();
        StoryEventRenderer.IsTitleMilestone(3).ShouldBeTrue();
        StoryEventRenderer.IsTitleMilestone(4).ShouldBeFalse();
        StoryEventRenderer.IsTitleMilestone(5).ShouldBeTrue();
        StoryEventRenderer.IsTitleMilestone(6).ShouldBeFalse();
        StoryEventRenderer.IsTitleMilestone(10).ShouldBeTrue();
        StoryEventRenderer.IsTitleMilestone(15).ShouldBeTrue();
        StoryEventRenderer.IsTitleMilestone(11).ShouldBeFalse();
    }

    [Fact]
    public async Task Emitter_Dedup_PreventsDuplicatesOnRetries()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Story Dedup", 5150UL, 6160UL, UniverseTestCatalog.Build());
            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            int athleteId = await context.SaveAthletes.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).FirstAsync();
            StoryEventPayload payload = new("Dedup Athlete", 1, "White League", 1, StageRank: 1);

            bool first = await StoryEventEmitter.TryEmitAsync(
                context, athleteId, StoryEventType.FirstStageWin, StoryEventEmitter.FirstDedup, 1, 1, payload);
            first.ShouldBeTrue();
            await context.SaveChangesAsync();

            bool retry = await StoryEventEmitter.TryEmitAsync(
                context, athleteId, StoryEventType.FirstStageWin, StoryEventEmitter.FirstDedup, 1, 1, payload);
            retry.ShouldBeFalse();
            await context.SaveChangesAsync();

            using SaveDbContext verify = store.OpenDbContext(created.Detail.SaveId);
            List<StoryEventEntity> rows = await verify.StoryEvents.AsNoTracking().ToListAsync();
            rows.Count(e => e.SaveAthleteId == athleteId).ShouldBe(1);

            bool milestone = await StoryEventEmitter.TryEmitAsync(
                verify, athleteId, StoryEventType.LeagueTitleMilestone, StoryEventEmitter.TitleDedup(2), 2, null,
                new StoryEventPayload("Dedup Athlete", 2, "White League", SeasonRank: 1, TitleCount: 2));
            milestone.ShouldBeTrue();
            await verify.SaveChangesAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteStage_EmitsFirstStageWin_WithStructuredContext()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Stage Story", 101UL, 202UL, UniverseTestCatalog.Build());
            int leagueId = await FirstLeagueIdAsync(store, created.Detail.SaveId);

            CompleteStageHandler handler = new(store);
            CompleteStageResponse response = await handler.HandleAsync(created.Detail.SaveId, leagueId);
            CompleteStageStanding winner = response.Standings.Single(s => s.StageRank == 1);

            List<StoryEventEntity> stories = await LoadStoriesAsync(store, created.Detail.SaveId);
            StoryEventEntity story = stories.Single(e =>
                e.SaveAthleteId == winner.AthleteId &&
                string.Equals(e.EventType, StoryEventType.FirstStageWin, StringComparison.Ordinal));
            story.SeasonNumber.ShouldBe(1);
            story.StageNumber.ShouldBe(1);
            string.Equals(story.DedupKey, StoryEventEmitter.FirstDedup, StringComparison.Ordinal).ShouldBeTrue();
            StoryEventPayload payload = StoryEventRenderer.Parse(story.ContextJson);
            string.Equals(payload.AthleteName, winner.Name, StringComparison.Ordinal).ShouldBeTrue();
            payload.StageRank.ShouldBe(1);

            ListRecentStoriesHandler query = new(store);
            ListRecentStoriesResponse feed = await query.HandleAsync(created.Detail.SaveId);
            feed.Stories.Count.ShouldBe(1);
            feed.Stories[0].Text.ShouldContain(winner.Name);
            string.Equals(feed.Stories[0].Text, StoryEventRenderer.Render(story.EventType, story.ContextJson), StringComparison.Ordinal)
                .ShouldBeTrue();

            int before = stories.Count;
            await Should.ThrowAsync<CompleteStageConflictException>(() => handler.HandleAsync(created.Detail.SaveId, leagueId));
            List<StoryEventEntity> after = await LoadStoriesAsync(store, created.Detail.SaveId);
            after.Count.ShouldBe(before);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteSeason_EmitsFirstLeagueTitlePerChampion()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Season Story", 7171UL, 8181UL, UniverseTestCatalog.Build());
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(created.Detail.SaveId);

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            List<SeasonStandingEntity> champions = await context.SeasonStandings
                .AsNoTracking().Where(e => e.IsChampion).ToListAsync();
            champions.Count.ShouldBe(8);

            List<StoryEventEntity> stories = await LoadStoriesAsync(store, created.Detail.SaveId);
            List<StoryEventEntity> titles = stories
                .Where(e => string.Equals(e.EventType, StoryEventType.FirstLeagueTitle, StringComparison.Ordinal)).ToList();
            titles.Count.ShouldBe(8);
            foreach (SeasonStandingEntity champion in champions)
            {
                titles.Any(e => e.SaveAthleteId == champion.SaveAthleteId).ShouldBeTrue();
            }

            stories.Count(e => string.Equals(e.EventType, StoryEventType.LeagueTitleMilestone, StringComparison.Ordinal)).ShouldBe(0);
            stories.Count(e => string.Equals(e.EventType, StoryEventType.FirstStageWin, StringComparison.Ordinal)).ShouldBeGreaterThan(0);

            ListRecentStoriesHandler query = new(store);
            ListRecentStoriesResponse feed = await query.HandleAsync(created.Detail.SaveId, 100);
            feed.Stories.Count.ShouldBeGreaterThanOrEqualTo(8);
            for (int i = 1; i < feed.Stories.Count; i++)
            {
                feed.Stories[i - 1].Id.ShouldBeGreaterThan(feed.Stories[i].Id);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PostseasonChain_EmitsPromotionSuperleagueAndPoolStories()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Postseason Story", 707UL, 808UL, UniverseTestCatalog.Build());
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(created.Detail.SaveId);

            RngStateEntity rngBefore = await ReadRngAsync(store, created.Detail.SaveId);
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);
            RngStateEntity rngAfter = await ReadRngAsync(store, created.Detail.SaveId);
            ((ulong)rngAfter.State).ShouldBe((ulong)rngBefore.State);

            List<StoryEventEntity> afterInaugural = await LoadStoriesAsync(store, created.Detail.SaveId);
            afterInaugural.Count(e => string.Equals(e.EventType, StoryEventType.Promotion, StringComparison.Ordinal)).ShouldBe(32);
            afterInaugural.Count(e => string.Equals(e.EventType, StoryEventType.FirstSuperleagueAppearance, StringComparison.Ordinal)).ShouldBe(32);
            foreach (StoryEventEntity promotion in afterInaugural
                .Where(e => string.Equals(e.EventType, StoryEventType.Promotion, StringComparison.Ordinal)))
            {
                promotion.SeasonNumber.ShouldBe(2);
                StoryEventRenderer.Parse(promotion.ContextJson).ToSeasonNumber.ShouldBe(2);
            }

            RebalanceFeedersHandler rebalance = new(store);
            await rebalance.HandleAsync(created.Detail.SaveId);

            List<StoryEventEntity> afterRebalance = await LoadStoriesAsync(store, created.Detail.SaveId);
            List<StoryEventEntity> comebacks = afterRebalance
                .Where(e => string.Equals(e.EventType, StoryEventType.ReturnFromPool, StringComparison.Ordinal)).ToList();
            comebacks.Count.ShouldBe(32);
            foreach (StoryEventEntity comeback in comebacks)
            {
                comeback.SeasonNumber.ShouldBe(2);
                StoryEventRenderer.Render(comeback.EventType, comeback.ContextJson)
                    .ShouldContain("returns from the common pool");
            }

            ListRecentStoriesHandler recent = new(store);
            ListRecentStoriesResponse feed = await recent.HandleAsync(created.Detail.SaveId, 5);
            feed.Stories.Count.ShouldBe(5);

            int anyAthlete = afterInaugural
                .First(e => string.Equals(e.EventType, StoryEventType.Promotion, StringComparison.Ordinal)).SaveAthleteId;
            ListAthleteStoriesHandler athleteQuery = new(store);
            ListRecentStoriesResponse athleteFeed = await athleteQuery.HandleAsync(created.Detail.SaveId, anyAthlete);
            athleteFeed.Stories.Count.ShouldBeGreaterThanOrEqualTo(2);
            athleteFeed.Stories.All(s => s.AthleteId == anyAthlete).ShouldBeTrue();
            athleteFeed.Stories.Any(s => string.Equals(s.EventType, StoryEventType.Promotion, StringComparison.Ordinal)).ShouldBeTrue();

            await Should.ThrowAsync<AthleteStoriesNotFoundException>(
                () => athleteQuery.HandleAsync(created.Detail.SaveId, 999999));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Qualifier_EmitsQualifierPromotionAndRelegationStories()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Qualifier Story", 9001UL, 7002UL, UniverseTestCatalog.Build());
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(created.Detail.SaveId);

            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);
            RebalanceFeedersHandler rebalance = new(store);
            await rebalance.HandleAsync(created.Detail.SaveId);

            await InsertSyntheticSeasonTwoStandingsAsync(store, created.Detail.SaveId);

            ResolveAutomaticMovementHandler movement = new(store);
            await movement.HandleAsync(created.Detail.SaveId);
            RunQualifierHandler qualifier = new(store);
            RunQualifierResponse qualifierResponse = await qualifier.HandleAsync(created.Detail.SaveId);
            qualifierResponse.Winners.ShouldBe(8);

            await AssertQualifierMovementStoriesAsync(store, created.Detail.SaveId);

            await rebalance.HandleAsync(created.Detail.SaveId);
            await AssertStoryKeysUniqueAsync(store, created.Detail.SaveId);

            int rerunPromotions = await CountByTypeAsync(store, created.Detail.SaveId, StoryEventType.Promotion);
            await Should.ThrowAsync<RunQualifierConflictException>(() => qualifier.HandleAsync(created.Detail.SaveId));
            int afterRerun = await CountByTypeAsync(store, created.Detail.SaveId, StoryEventType.Promotion);
            afterRerun.ShouldBe(rerunPromotions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static async Task AssertQualifierMovementStoriesAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<QualifierStandingEntity> standings = await context.QualifierStandings.AsNoTracking().ToListAsync().ConfigureAwait(false);
        List<int> challengerWinners = standings
            .Where(s => s.Role == (int)QualifierRole.Challenger && s.IsQualified)
            .Select(s => s.SaveAthleteId).ToList();
        List<int> failedIncumbents = standings
            .Where(s => s.Role == (int)QualifierRole.Incumbent && !s.IsQualified)
            .Select(s => s.SaveAthleteId).ToList();

        List<StoryEventEntity> stories = await context.StoryEvents.AsNoTracking().ToListAsync().ConfigureAwait(false);
        List<StoryEventEntity> promotions = stories
            .Where(e => string.Equals(e.EventType, StoryEventType.Promotion, StringComparison.Ordinal) && e.SeasonNumber == 3 && StoryEventRenderer.Parse(e.ContextJson).ViaQualifier == true).ToList();
        promotions.Count.ShouldBe(challengerWinners.Count);
        foreach (int athleteId in challengerWinners)
        {
            StoryEventEntity story = promotions.Single(e => e.SaveAthleteId == athleteId);
            StoryEventRenderer.Parse(story.ContextJson).ViaQualifier.ShouldBe(true);
            StoryEventRenderer.Render(story.EventType, story.ContextJson).ShouldContain("via the qualifier");
        }

        List<StoryEventEntity> relegations = stories
            .Where(e => string.Equals(e.EventType, StoryEventType.Relegation, StringComparison.Ordinal) && e.SeasonNumber == 3 && StoryEventRenderer.Parse(e.ContextJson).ViaQualifier == true).ToList();
        relegations.Count.ShouldBe(failedIncumbents.Count);
        foreach (int athleteId in failedIncumbents)
        {
            StoryEventEntity story = relegations.Single(e => e.SaveAthleteId == athleteId);
            StoryEventRenderer.Parse(story.ContextJson).ViaQualifier.ShouldBe(true);
        }

        stories.Count(e => string.Equals(e.EventType, StoryEventType.FirstSuperleagueAppearance, StringComparison.Ordinal))
            .ShouldBeGreaterThanOrEqualTo(32);
    }

    internal static async Task AssertStoryKeysUniqueAsync(SaveStore store, Guid saveId)
    {
        List<StoryEventEntity> all = await LoadStoriesAsync(store, saveId).ConfigureAwait(false);
        all.Select(e => string.Join(
            "|",
            e.SaveAthleteId.ToString(CultureInfo.InvariantCulture),
            e.EventType,
            e.DedupKey)).Distinct(StringComparer.Ordinal).Count().ShouldBe(all.Count);
    }

    internal static async Task<int> CountByTypeAsync(SaveStore store, Guid saveId, string eventType)
    {
        List<StoryEventEntity> all = await LoadStoriesAsync(store, saveId).ConfigureAwait(false);
        return all.Count(e => string.Equals(e.EventType, eventType, StringComparison.Ordinal));
    }

    internal static async Task<List<StoryEventEntity>> LoadStoriesAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.StoryEvents.AsNoTracking().ToListAsync().ConfigureAwait(false);
    }

    private static async Task InsertSyntheticSeasonTwoStandingsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == seasonTwo.Id)
            .OrderBy(e => e.Id)
            .ToListAsync().ConfigureAwait(false);
        leagues.Count.ShouldBe(9);

        foreach (LeagueEntity league in leagues)
        {
            List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
                .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == league.Id)
                .ToListAsync().ConfigureAwait(false);
            memberships.Count.ShouldBe(32);
            List<SeasonMembershipEntity> ordered = memberships.OrderBy(m => m.SaveAthleteId).ToList();
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

    private static async Task<RngStateEntity> ReadRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
    }

    private static async Task<int> FirstLeagueIdAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        LeagueEntity league = await context.Leagues.AsNoTracking().OrderBy(e => e.Id).FirstAsync().ConfigureAwait(false);
        return league.Id;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-stories-" + Guid.NewGuid().ToString("N"));
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
