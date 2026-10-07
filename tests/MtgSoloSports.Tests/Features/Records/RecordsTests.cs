using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.GetHallOfFame;
using MtgSoloSports.Features.Records.GetRecords;
using MtgSoloSports.Features.Records.ListHonours;
using MtgSoloSports.Features.Stories;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Records;

public sealed class RecordsTests
{
    [Fact]
    public async Task AfterFullSeason_PersistsEightFeederHonoursAndRecordStories()
    {
        // MSS-067: shared Season 1 template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-records-");
        try
        {

            await AssertPodiumHonoursAsync(store, saveId);
            await AssertWinOnlyRecordsAsync(store, saveId);
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
        honours.Count.ShouldBe(72);
        honours.Count(h => h.Kind == (int)HonourKind.FeederTitle).ShouldBe(24);
        honours.Count(h => h.Kind == (int)HonourKind.FeederRunnerUp).ShouldBe(24);
        honours.Count(h => h.Kind == (int)HonourKind.FeederThirdPlace).ShouldBe(24);
        honours.All(h => h.SeasonNumber == 1).ShouldBeTrue();

        List<SeasonStandingEntity> champions = await context.SeasonStandings
            .AsNoTracking().Where(e => e.IsChampion).ToListAsync().ConfigureAwait(false);
        champions.Count.ShouldBe(24);
        foreach (SeasonStandingEntity champion in champions)
        {
            honours.Any(h =>
                h.SeasonId == champion.SeasonId &&
                h.LeagueId == champion.LeagueId &&
                h.SaveAthleteId == champion.SaveAthleteId).ShouldBeTrue();
        }

        List<SeasonStandingEntity> podiums = await context.SeasonStandings
            .AsNoTracking().Where(e => e.SeasonRank >= 1 && e.SeasonRank <= 3).ToListAsync().ConfigureAwait(false);
        podiums.Count.ShouldBe(72);

        ListHonoursHandler honoursHandler = new(store);
        ListHonoursResponse honoursResponse = await honoursHandler.HandleAsync(saveId).ConfigureAwait(false);
        honoursResponse.Honours.Count.ShouldBe(72);
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
        feeder.Holders.Count.ShouldBe(24);
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
        // MSS-067: shared Season 1 template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-records-");
        try
        {

            using SaveDbContext context = store.OpenDbContext(saveId);
            int before = await context.Honours.CountAsync();
            before.ShouldBe(72);
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
        // MSS-067: shared Season 1 template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-records-");
        try
        {

            int before;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                before = (await context.StoryEvents.AsNoTracking().ToListAsync()).Count(e => string.Equals(e.EventType, StoryEventType.NewRecord, StringComparison.Ordinal));
            }

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                SeasonEntity season = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1);
                bool emitted = await RecordStoryEmitter.EmitBreaksAsync(context, season, CancellationToken.None);
                emitted.ShouldBeFalse();
                await context.SaveChangesAsync();
            }

            using (SaveDbContext verify = store.OpenDbContext(saveId))
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
        // MSS-067: shared Season 1 template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-records-");
        try
        {

            int stageRecordBefore;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                GetRecordsHandler handler = new(store);
                GetRecordsResponse before = await handler.HandleAsync(saveId);
                stageRecordBefore = before.Records.Single(r => string.Equals(r.RecordKey, RecordKey.StageWins, StringComparison.Ordinal)).Value;
                stageRecordBefore.ShouldBeGreaterThan(0);
            }

            int boostedAthlete;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                AthleteCareerEntity career = await context.AthleteCareers.OrderBy(e => e.SaveAthleteId).FirstAsync();
                boostedAthlete = career.SaveAthleteId;
                career.StageWins = checked(stageRecordBefore + 5);
                career.RoundWins = checked(career.RoundWins + 100);
                await context.SaveChangesAsync();
            }

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                SeasonEntity season = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1);
                bool emitted = await RecordStoryEmitter.EmitBreaksAsync(context, season, CancellationToken.None);
                emitted.ShouldBeTrue();
                await context.SaveChangesAsync();
            }

            using (SaveDbContext verify = store.OpenDbContext(saveId))
            {
                GetRecordsHandler handler = new(store);
                GetRecordsResponse after = await handler.HandleAsync(saveId);
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
        // MSS-067: shared Season 1 template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-records-");
        try
        {

            using SaveDbContext context = store.OpenDbContext(saveId);
            int payloadRows = await context.Rounds.CountAsync();
            payloadRows.ShouldBeGreaterThan(0);

            Dictionary<int, int> careerStageWins = await context.AthleteCareers
                .AsNoTracking()
                .ToDictionaryAsync(e => e.SaveAthleteId, e => e.StageWins);
            int expectedMax = careerStageWins.Values.Max();

            GetRecordsHandler handler = new(store);
            GetRecordsResponse records = await handler.HandleAsync(saveId);
            records.Records.Single(r => string.Equals(r.RecordKey, RecordKey.StageWins, StringComparison.Ordinal)).Value.ShouldBe(expectedMax);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
