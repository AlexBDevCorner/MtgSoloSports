using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.History.GetRoundReplay;
using MtgSoloSports.Features.History.GetSeasonTable;
using MtgSoloSports.Features.History.GetStageStandings;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.History.ListRounds;
using MtgSoloSports.Features.History.ListSeasons;
using MtgSoloSports.Features.History.ListStages;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.History;

public sealed class HistoryReplayTests
{
    [Fact]
    public async Task Replay_ReturnsOriginallyPersistedResult_AfterFutureSeasonsAdvanced()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("History Replay", 101UL, 202UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            AdvanceRoundHandler advance = new(store);
            AdvanceRoundResponse first = await advance.HandleAsync(saveId, leagueId);

            GetHistoryRoundReplayHandler replay = new(store);
            GetHistoryRoundReplayResponse before = await replay.HandleAsync(saveId, 1, leagueId, 1, 1);
            AssertSamePresentation(first, before);
            await AssertReplayLeavesRngAloneAsync(store, saveId, replay, leagueId, before);
            int roundsAfter = await AdvanceToSeasonTwoAsync(store, saveId);
            await AssertReplayAfterBulkAsync(store, saveId, replay, leagueId, before, roundsAfter);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Replay_DoesNotMutateSaveState()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("History Stable", 303UL, 404UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            AdvanceRoundHandler advance = new(store);
            await advance.HandleAsync(saveId, leagueId);
            await advance.HandleAsync(saveId, leagueId);

            GetHistoryRoundReplayHandler replay = new(store);
            (ulong rngBeforeState, ulong rngBeforeStream) = await LoadRngAsync(store, saveId);
            int roundsBefore = await CountRoundsAsync(store, saveId);
            int stagesBefore = await CountStagesAsync(store, saveId);
            int standingsBefore = await CountStageStandingsAsync(store, saveId);

            await replay.HandleAsync(saveId, 1, leagueId, 1, 1);
            await replay.HandleAsync(saveId, 1, leagueId, 1, 2);

            (ulong rngAfterState, ulong rngAfterStream) = await LoadRngAsync(store, saveId);
            rngAfterState.ShouldBe(rngBeforeState);
            rngAfterStream.ShouldBe(rngBeforeStream);
            (await CountRoundsAsync(store, saveId)).ShouldBe(roundsBefore);
            (await CountStagesAsync(store, saveId)).ShouldBe(stagesBefore);
            (await CountStageStandingsAsync(store, saveId)).ShouldBe(standingsBefore);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Lists_DoNotDecompressPayloads_ReplayDetectsCorruption()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("History Corrupt", 505UL, 606UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            AdvanceRoundHandler advance = new(store);
            await advance.HandleAsync(saveId, leagueId);

            CorruptFirstPayload(store, saveId);

            ListHistoryRoundsHandler rounds = new(store);
            ListHistoryRoundsResponse summaries = await rounds.HandleAsync(saveId, 1, leagueId, 1);
            summaries.Rounds.Count.ShouldBe(1);

            ListHistorySeasonsHandler seasons = new(store);
            (await seasons.HandleAsync(saveId)).Seasons.Count.ShouldBe(1);

            GetHistoryRoundReplayHandler replay = new(store);
            await Should.ThrowAsync<InvalidOperationException>(() =>
                replay.HandleAsync(saveId, 1, leagueId, 1, 1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PersistedRoundPayload_IsCompressed_AndChainsAcrossRounds()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("History Compressed", 111UL, 222UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            AdvanceRoundHandler advance = new(store);
            AdvanceRoundResponse first = await advance.HandleAsync(saveId, leagueId);

            RoundEntity storedFirst;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                storedFirst = await context.Rounds.AsNoTracking().SingleAsync(e => e.LeagueId == leagueId && e.RoundNumber == 1);
            }

            storedFirst.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();
            RoundPayloadDocument decoded = RoundPayloadCodec.DecodeRound(storedFirst.PayloadJson);
            decoded.Checksum.ShouldBe(first.PayloadChecksum);
            decoded.Placements.Count.ShouldBe(32);

            // Second round must chain cumulative totals from the compressed first payload.
            AdvanceRoundResponse second = await advance.HandleAsync(saveId, leagueId);
            second.RoundNumber.ShouldBe(2);

            RoundEntity storedSecond;
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                storedSecond = await context.Rounds.AsNoTracking().SingleAsync(e => e.LeagueId == leagueId && e.RoundNumber == 2);
            }

            storedSecond.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();

            GetHistoryRoundReplayHandler replay = new(store);
            GetHistoryRoundReplayResponse replayed = await replay.HandleAsync(saveId, 1, leagueId, 1, 1);
            replayed.PayloadChecksum.ShouldBe(first.PayloadChecksum);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HistoryNavigation_ResolvesSeasonCompetitionStageRoundChain()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("History Chain", 707UL, 808UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            int leagueId = await FirstLeagueIdAsync(store, saveId);

            CompleteStageHandler complete = new(store);
            await complete.HandleAsync(saveId, leagueId);
            await AssertNavigationChainAsync(store, saveId, leagueId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertReplayLeavesRngAloneAsync(
        SaveStore store,
        Guid saveId,
        GetHistoryRoundReplayHandler replay,
        int leagueId,
        GetHistoryRoundReplayResponse before)
    {
        (ulong rngBeforeState, ulong rngBeforeStream) = await LoadRngAsync(store, saveId).ConfigureAwait(false);
        GetHistoryRoundReplayResponse reread = await replay.HandleAsync(saveId, 1, leagueId, 1, 1).ConfigureAwait(false);
        (ulong rngAfterState, ulong rngAfterStream) = await LoadRngAsync(store, saveId).ConfigureAwait(false);
        reread.PayloadChecksum.ShouldBe(before.PayloadChecksum);
        rngAfterState.ShouldBe(rngBeforeState);
        rngAfterStream.ShouldBe(rngBeforeStream);
    }

    private static async Task<int> AdvanceToSeasonTwoAsync(SaveStore store, Guid saveId)
    {
        int roundsBefore = await CountRoundsAsync(store, saveId).ConfigureAwait(false);
        SimulateSeasonsHandler bulk = new(store);
        SimulateSeasonsResponse simulated = await bulk.HandleAsync(saveId, new SimulateSeasonsRequest(1)).ConfigureAwait(false);
        simulated.SeasonsCompleted.ShouldBe(1);
        simulated.EndSeasonNumber.ShouldBe(2);
        int roundsAfter = await CountRoundsAsync(store, saveId).ConfigureAwait(false);
        roundsAfter.ShouldBeGreaterThan(roundsBefore);
        return roundsAfter;
    }

    private static async Task AssertReplayAfterBulkAsync(
        SaveStore store,
        Guid saveId,
        GetHistoryRoundReplayHandler replay,
        int leagueId,
        GetHistoryRoundReplayResponse before,
        int roundsAfter)
    {
        GetHistoryRoundReplayResponse after = await replay.HandleAsync(saveId, 1, leagueId, 1, 1).ConfigureAwait(false);
        AssertReplayEqual(after, before);
        await AssertNavigationAfterBulkAsync(store, saveId, leagueId, before).ConfigureAwait(false);
        await AssertReplayStillReadOnlyAsync(store, saveId, replay, leagueId, roundsAfter).ConfigureAwait(false);
    }

    private static async Task AssertNavigationAfterBulkAsync(
        SaveStore store,
        Guid saveId,
        int leagueId,
        GetHistoryRoundReplayResponse before)
    {
        ListHistorySeasonsHandler seasons = new(store);
        ListHistorySeasonsResponse seasonList = await seasons.HandleAsync(saveId).ConfigureAwait(false);
        seasonList.Seasons.Count.ShouldBeGreaterThanOrEqualTo(2);
        seasonList.Seasons.Single(s => s.SeasonNumber == 1).IsComplete.ShouldBeTrue();

        ListHistoryCompetitionsHandler competitions = new(store);
        ListHistoryCompetitionsResponse competitionList = await competitions.HandleAsync(saveId, 1).ConfigureAwait(false);
        competitionList.Competitions.Count.ShouldBe(24);
        competitionList.Competitions.ShouldContain(c => c.LeagueId == leagueId);

        ListHistoryStagesHandler stages = new(store);
        ListHistoryStagesResponse stageList = await stages.HandleAsync(saveId, 1, leagueId).ConfigureAwait(false);
        stageList.Stages.Count.ShouldBe(32);
        stageList.Stages.ShouldAllBe(s => s.IsComplete);

        ListHistoryRoundsHandler rounds = new(store);
        ListHistoryRoundsResponse roundList = await rounds.HandleAsync(saveId, 1, leagueId, 1).ConfigureAwait(false);
        roundList.Rounds.Count.ShouldBe(16);
        roundList.Rounds[0].PayloadChecksum.ShouldBe(before.PayloadChecksum);

        GetHistoryStageStandingsHandler standings = new(store);
        GetHistoryStageStandingsResponse stageStandings = await standings.HandleAsync(saveId, 1, leagueId, 1).ConfigureAwait(false);
        stageStandings.Standings.Count.ShouldBe(32);

        GetHistorySeasonTableHandler table = new(store);
        GetHistorySeasonTableResponse seasonTable = await table.HandleAsync(saveId, 1, leagueId).ConfigureAwait(false);
        seasonTable.Standings.Count.ShouldBe(32);
        seasonTable.IsSeasonComplete.ShouldBeTrue();
    }

    private static async Task AssertReplayStillReadOnlyAsync(
        SaveStore store,
        Guid saveId,
        GetHistoryRoundReplayHandler replay,
        int leagueId,
        int roundsAfter)
    {
        (ulong rngFinalBeforeState, ulong rngFinalBeforeStream) = await LoadRngAsync(store, saveId).ConfigureAwait(false);
        await replay.HandleAsync(saveId, 1, leagueId, 1, 1).ConfigureAwait(false);
        (ulong rngFinalAfterState, ulong rngFinalAfterStream) = await LoadRngAsync(store, saveId).ConfigureAwait(false);
        rngFinalAfterState.ShouldBe(rngFinalBeforeState);
        rngFinalAfterStream.ShouldBe(rngFinalBeforeStream);
        (await CountRoundsAsync(store, saveId).ConfigureAwait(false)).ShouldBe(roundsAfter);
    }

    private static async Task AssertNavigationChainAsync(SaveStore store, Guid saveId, int leagueId)
    {
        ListHistorySeasonsHandler seasons = new(store);
        ListHistorySeasonsResponse seasonList = await seasons.HandleAsync(saveId).ConfigureAwait(false);
        seasonList.Seasons.Count.ShouldBe(1);
        seasonList.Seasons[0].SeasonNumber.ShouldBe(1);

        ListHistoryCompetitionsHandler competitions = new(store);
        ListHistoryCompetitionsResponse competitionList = await competitions.HandleAsync(saveId, 1).ConfigureAwait(false);
        competitionList.Competitions.Count.ShouldBe(24);

        ListHistoryStagesHandler stages = new(store);
        ListHistoryStagesResponse stageList = await stages.HandleAsync(saveId, 1, leagueId).ConfigureAwait(false);
        stageList.Stages.Count.ShouldBeGreaterThanOrEqualTo(1);
        stageList.Stages.Single(s => s.StageNumber == 1).IsComplete.ShouldBeTrue();
        stageList.Stages.Single(s => s.StageNumber == 1).HasStandings.ShouldBeTrue();

        ListHistoryRoundsHandler rounds = new(store);
        ListHistoryRoundsResponse roundList = await rounds.HandleAsync(saveId, 1, leagueId, 1).ConfigureAwait(false);
        roundList.Rounds.Count.ShouldBe(16);
        roundList.IsStageComplete.ShouldBeTrue();

        GetHistoryRoundReplayHandler replay = new(store);
        GetHistoryRoundReplayResponse replayed = await replay.HandleAsync(saveId, 1, leagueId, 1, 16).ConfigureAwait(false);
        replayed.Placements.Count.ShouldBe(32);
        replayed.Placements.Select(p => p.Position).ShouldBe(Enumerable.Range(1, 32).ToList());

        GetHistorySeasonTableHandler table = new(store);
        await Should.ThrowAsync<HistoryNotFoundException>(() => table.HandleAsync(saveId, 1, leagueId)).ConfigureAwait(false);

        await Should.ThrowAsync<HistoryNotFoundException>(() => competitions.HandleAsync(saveId, 99)).ConfigureAwait(false);
        await Should.ThrowAsync<HistoryNotFoundException>(() =>
            replay.HandleAsync(saveId, 1, leagueId, 2, 1)).ConfigureAwait(false);
        await Should.ThrowAsync<ArgumentException>(() =>
            replay.HandleAsync(saveId, 1, leagueId, 1, 99)).ConfigureAwait(false);
    }

    private static void AssertSamePresentation(AdvanceRoundResponse live, GetHistoryRoundReplayResponse replayed)
    {
        replayed.SeasonNumber.ShouldBe(live.SeasonNumber);
        replayed.LeagueId.ShouldBe(live.LeagueId);
        replayed.StageNumber.ShouldBe(live.StageNumber);
        replayed.RoundNumber.ShouldBe(live.RoundNumber);
        replayed.RulesVersion.ShouldBe(live.RulesVersion);
        replayed.PayloadChecksum.ShouldBe(live.PayloadChecksum);
        replayed.Placements.Count.ShouldBe(live.Placements.Count);
        AssertPlacementsEqual(replayed.Placements, live.Placements);
    }

    private static void AssertReplayEqual(GetHistoryRoundReplayResponse after, GetHistoryRoundReplayResponse before)
    {
        after.PayloadChecksum.ShouldBe(before.PayloadChecksum);
        after.RulesVersion.ShouldBe(before.RulesVersion);
        after.RngBeforeState.ShouldBe(before.RngBeforeState);
        after.RngAfterState.ShouldBe(before.RngAfterState);
        after.Placements.Count.ShouldBe(32);
        for (int i = 0; i < 32; i++)
        {
            var want = before.Placements[i];
            var got = after.Placements[i];
            got.AthleteId.ShouldBe(want.AthleteId);
            got.Name.ShouldBe(want.Name);
            got.Position.ShouldBe(want.Position);
            got.BaseThousandths.ShouldBe(want.BaseThousandths);
            got.ActiveBonusThousandths.ShouldBe(want.ActiveBonusThousandths);
            got.FinalThousandths.ShouldBe(want.FinalThousandths);
            got.CumulativeBeforeThousandths.ShouldBe(want.CumulativeBeforeThousandths);
            got.CumulativeAfterThousandths.ShouldBe(want.CumulativeAfterThousandths);
            got.RankBefore.ShouldBe(want.RankBefore);
            got.RankAfter.ShouldBe(want.RankAfter);
            got.RankMovement.ShouldBe(want.RankMovement);
        }
    }

    private static void AssertPlacementsEqual(
        IReadOnlyList<HistoryRoundPlacement> replayed,
        IReadOnlyList<AdvanceRoundPlacement> live)
    {
        for (int i = 0; i < live.Count; i++)
        {
            var want = live[i];
            var got = replayed[i];
            got.AthleteId.ShouldBe(want.AthleteId);
            got.Name.ShouldBe(want.Name);
            got.Position.ShouldBe(want.Position);
            got.BaseThousandths.ShouldBe(want.BaseThousandths);
            got.ActiveBonusThousandths.ShouldBe(want.ActiveBonusThousandths);
            got.FinalThousandths.ShouldBe(want.FinalThousandths);
            got.CumulativeBeforeThousandths.ShouldBe(want.CumulativeBeforeThousandths);
            got.CumulativeAfterThousandths.ShouldBe(want.CumulativeAfterThousandths);
            got.RankBefore.ShouldBe(want.RankBefore);
            got.RankAfter.ShouldBe(want.RankAfter);
            got.RankMovement.ShouldBe(want.RankMovement);
        }
    }

    private static void CorruptFirstPayload(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RoundEntity round = context.Rounds.Single(e => e.RoundNumber == 1);
        round.PayloadJson = "{corrupt";
        context.SaveChanges();
    }

    private static async Task<(ulong State, ulong Stream)> LoadRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return ((ulong)rng.State, (ulong)rng.Stream);
        }
    }

    private static async Task<int> CountRoundsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Rounds.AsNoTracking().CountAsync().ConfigureAwait(false);
    }

    private static async Task<int> CountStagesAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Stages.AsNoTracking().CountAsync().ConfigureAwait(false);
    }

    private static async Task<int> CountStageStandingsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.StageStandings.AsNoTracking().CountAsync().ConfigureAwait(false);
    }

    private static async Task<int> FirstLeagueIdAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        LeagueEntity league = await context.Leagues.AsNoTracking().OrderBy(e => e.Id).FirstAsync().ConfigureAwait(false);
        return league.Id;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-history-" + Guid.NewGuid().ToString("N"));
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
