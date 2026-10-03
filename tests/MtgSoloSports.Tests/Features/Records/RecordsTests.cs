using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.GetHallOfFame;
using MtgSoloSports.Features.Records.GetRecords;
using MtgSoloSports.Features.Records.ListHonours;
using MtgSoloSports.Features.Simulation.CompleteSeason;
using MtgSoloSports.Features.Stories;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Records;

public sealed class RecordsTests
{
    [Fact]
    public async Task AfterFullSeason_PersistsEightFeederHonoursAndRecordStories()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Records Season One", 424201UL, 848402UL, UniverseTestCatalog.Build());
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(created.Detail.SaveId);

            await AssertPodiumHonoursAsync(store, created.Detail.SaveId);
            await AssertWinOnlyRecordsAsync(store, created.Detail.SaveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertPodiumHonoursAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<HonourEntity> honours = await context.Honours.AsNoTracking().ToListAsync().ConfigureAwait(false);
        honours.Count.ShouldBe(24);
        honours.Count(h => h.Kind == (int)HonourKind.FeederTitle).ShouldBe(8);
        honours.Count(h => h.Kind == (int)HonourKind.FeederRunnerUp).ShouldBe(8);
        honours.Count(h => h.Kind == (int)HonourKind.FeederThirdPlace).ShouldBe(8);
        honours.All(h => h.SeasonNumber == 1).ShouldBeTrue();

        List<SeasonStandingEntity> champions = await context.SeasonStandings
            .AsNoTracking().Where(e => e.IsChampion).ToListAsync().ConfigureAwait(false);
        champions.Count.ShouldBe(8);
        foreach (SeasonStandingEntity champion in champions)
        {
            honours.Any(h =>
                h.SeasonId == champion.SeasonId &&
                h.LeagueId == champion.LeagueId &&
                h.SaveAthleteId == champion.SaveAthleteId).ShouldBeTrue();
        }

        List<SeasonStandingEntity> podiums = await context.SeasonStandings
            .AsNoTracking().Where(e => e.SeasonRank >= 1 && e.SeasonRank <= 3).ToListAsync().ConfigureAwait(false);
        podiums.Count.ShouldBe(24);

        ListHonoursHandler honoursHandler = new(store);
        ListHonoursResponse honoursResponse = await honoursHandler.HandleAsync(saveId).ConfigureAwait(false);
        honoursResponse.Honours.Count.ShouldBe(24);
    }

    private static async Task AssertWinOnlyRecordsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<StoryEventEntity> stories = await context.StoryEvents.AsNoTracking().ToListAsync().ConfigureAwait(false);
        List<StoryEventEntity> recordStories = stories
            .Where(e => string.Equals(e.EventType, StoryEventType.NewRecord, StringComparison.Ordinal))
            .ToList();
        recordStories.Count.ShouldBeGreaterThan(0);
        recordStories.Any(e => e.ContextJson.Contains(RecordKey.FeederTitles, StringComparison.Ordinal)).ShouldBeTrue();

        GetRecordsHandler recordsHandler = new(store);
        GetRecordsResponse records = await recordsHandler.HandleAsync(saveId).ConfigureAwait(false);
        records.Records.Count.ShouldBe(RecordKey.All.Count);
        RecordEntry feeder = records.Records.Single(r => string.Equals(r.RecordKey, RecordKey.FeederTitles, StringComparison.Ordinal));
        feeder.Value.ShouldBe(1);
        feeder.Holders.Count.ShouldBe(8);
        feeder.IsVacant.ShouldBeFalse();
        records.Records.Single(r => string.Equals(r.RecordKey, RecordKey.SuperleagueTitles, StringComparison.Ordinal)).IsVacant.ShouldBeTrue();
        records.RecentHistory.Count.ShouldBeGreaterThan(0);

        GetHallOfFameHandler fameHandler = new(store);
        GetHallOfFameResponse fame = await fameHandler.HandleAsync(saveId).ConfigureAwait(false);
        fame.Leaders.Count.ShouldBeGreaterThan(0);
        fame.Leaders[0].Rank.ShouldBe(1);
        fame.Leaders[0].TotalTitles.ShouldBe(1);
        fame.TotalAthletes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task HonourRebuild_IsIdempotent()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Records Rebuild", 777001UL, 888002UL, UniverseTestCatalog.Build());
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(created.Detail.SaveId);

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            int before = await context.Honours.CountAsync();
            before.ShouldBe(24);
            await HonourUpdater.RebuildAllAsync(context, CancellationToken.None);
            await context.SaveChangesAsync();
            int after = await context.Honours.CountAsync();
            after.ShouldBe(before);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordEmitter_IsIdempotentWithoutNewBreaks()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Records Idempotent", 111003UL, 222004UL, UniverseTestCatalog.Build());
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(created.Detail.SaveId);

            int before;
            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                before = (await context.StoryEvents.AsNoTracking().ToListAsync()).Count(e => string.Equals(e.EventType, StoryEventType.NewRecord, StringComparison.Ordinal));
            }

            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                SeasonEntity season = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1);
                bool emitted = await RecordStoryEmitter.EmitBreaksAsync(context, season, CancellationToken.None);
                emitted.ShouldBeFalse();
                await context.SaveChangesAsync();
            }

            using (SaveDbContext verify = store.OpenDbContext(created.Detail.SaveId))
            {
                int after = (await verify.StoryEvents.AsNoTracking().ToListAsync()).Count(e => string.Equals(e.EventType, StoryEventType.NewRecord, StringComparison.Ordinal));
                after.ShouldBe(before);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordEmitter_ReplacementEmitsOnlyForOutrightBreak()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Records Replacement", 333005UL, 444006UL, UniverseTestCatalog.Build());
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(created.Detail.SaveId);

            int stageRecordBefore;
            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                GetRecordsHandler handler = new(store);
                GetRecordsResponse before = await handler.HandleAsync(created.Detail.SaveId);
                stageRecordBefore = before.Records.Single(r => string.Equals(r.RecordKey, RecordKey.StageWins, StringComparison.Ordinal)).Value;
                stageRecordBefore.ShouldBeGreaterThan(0);
            }

            int boostedAthlete;
            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                AthleteCareerEntity career = await context.AthleteCareers.OrderBy(e => e.SaveAthleteId).FirstAsync();
                boostedAthlete = career.SaveAthleteId;
                career.StageWins = checked(stageRecordBefore + 5);
                career.RoundWins = checked(career.RoundWins + 100);
                await context.SaveChangesAsync();
            }

            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                SeasonEntity season = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1);
                bool emitted = await RecordStoryEmitter.EmitBreaksAsync(context, season, CancellationToken.None);
                emitted.ShouldBeTrue();
                await context.SaveChangesAsync();
            }

            using (SaveDbContext verify = store.OpenDbContext(created.Detail.SaveId))
            {
                GetRecordsHandler handler = new(store);
                GetRecordsResponse after = await handler.HandleAsync(created.Detail.SaveId);
                after.Records.Single(r => string.Equals(r.RecordKey, RecordKey.StageWins, StringComparison.Ordinal)).Value.ShouldBe(stageRecordBefore + 5);
                after.Records.Single(r => string.Equals(r.RecordKey, RecordKey.StageWins, StringComparison.Ordinal)).Holders.Select(h => h.AthleteId).ShouldBe([boostedAthlete]);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Records_DoNotReadRoundPayloads_ValuesMatchNormalizedStandings()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Records No Payloads", 555007UL, 666008UL, UniverseTestCatalog.Build());
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(created.Detail.SaveId);

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            int payloadRows = await context.Rounds.CountAsync();
            payloadRows.ShouldBeGreaterThan(0);

            Dictionary<int, int> careerStageWins = await context.AthleteCareers
                .AsNoTracking()
                .ToDictionaryAsync(e => e.SaveAthleteId, e => e.StageWins);
            int expectedMax = careerStageWins.Values.Max();

            GetRecordsHandler handler = new(store);
            GetRecordsResponse records = await handler.HandleAsync(created.Detail.SaveId);
            records.Records.Single(r => string.Equals(r.RecordKey, RecordKey.StageWins, StringComparison.Ordinal)).Value.ShouldBe(expectedMax);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-records-" + Guid.NewGuid().ToString("N"));
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
