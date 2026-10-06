using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Leagues.UpgradeToTiered;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Leagues;

/// <summary>
/// MSS-057 v1-to-tiered safe-upgrade coverage: history preservation, F1
/// preservation, F2/F3 seeding, pool counts, idempotency/RNG safety, and
/// safe-boundary refusal.
/// </summary>
public sealed class UpgradeToTieredTests
{
    [Fact]
    public async Task Upgrade_AtCupComplete_PreservesHistory_SeedsF2F3_Idempotent()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await CreateV1FixtureAsync(store, 4242UL, 5656UL);
            await CompleteSeasonOneAsync(store, saveId);
            await DriveToCupCompleteAsync(store, saveId);

            HistorySnapshot before = await CaptureHistoryAsync(store, saveId);
            HashSet<int> f1Before = await LoadNextF1Async(store, saveId);

            UpgradeToTieredHandler handler = new(store);
            UpgradeToTieredResponse first = await handler.HandleAsync(saveId);

            AssertFirstResponse(first);
            await AssertUpgradedAsync(store, saveId, first, f1Before);
            await AssertHistoryPreservedAsync(store, saveId, before, first);
            await AssertIdempotentAsync(store, saveId, first);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Upgrade_LiveSeason_RefusesWithoutPartialUpgrade()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await CreateV1FixtureAsync(store, 111UL, 222UL);

            UpgradeToTieredHandler handler = new(store);
            await Should.ThrowAsync<UpgradeToTieredConflictException>(() => handler.HandleAsync(saveId));

            using SaveDbContext context = store.OpenDbContext(saveId);
            RulesSnapshotEntity rulesRow = await context.RulesSnapshots.AsNoTracking().SingleAsync(e => e.Id == 1);
            rulesRow.RulesVersion.ShouldBe(RulesV1.RulesVersion);
            int feeders = await context.Leagues.CountAsync(e => e.Kind == (int)LeagueKind.Feeder);
            feeders.ShouldBe(8);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Upgrade_BeforeCupComplete_RefusesWithoutPartialUpgrade()
    {
        var (store, root) = CreateStore();
        try
        {
            Guid saveId = await CreateV1FixtureAsync(store, 333UL, 444UL);
            await CompleteSeasonOneAsync(store, saveId);

            AdvanceToNextEventHandler advance = new(store);
            var inaugural = await advance.HandleAsync(saveId);
            inaugural.ExecutedAction.ShouldNotBeNull();

            UpgradeToTieredHandler handler = new(store);
            await Should.ThrowAsync<UpgradeToTieredConflictException>(() => handler.HandleAsync(saveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertFirstResponse(UpgradeToTieredResponse first)
    {
        first.SourceRulesVersion.ShouldBe(RulesV1.RulesVersion);
        first.TargetRulesVersion.ShouldBe(RulesV2.RulesVersion);
        first.AlreadyApplied.ShouldBeFalse();
        first.CreatedLeagues.Count.ShouldBe(16);
        first.F2Seeds.Count.ShouldBe(256);
        first.F3Seeds.Count.ShouldBe(256);
        first.MovementCount.ShouldBe(512);
        first.Checksum.Length.ShouldBe(64);
        first.RngAfterState.ShouldNotBe(first.RngBeforeState);
        first.PoolCount.ShouldBe(1248);
        foreach (UpgradePoolCount pool in first.RemainingPools)
        {
            pool.Count.ShouldBe(156);
        }
    }

    private sealed record HistorySnapshot(
        int Stages,
        int Seasons,
        int Rounds,
        int QualifierRounds,
        int QualifierStandings,
        int Movements,
        ulong Rng,
        string Rules);

    private static async Task<Guid> CreateV1FixtureAsync(SaveStore store, ulong seed, ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync("V1 Fixture", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;

        using SaveDbContext context = store.OpenDbContext(saveId);
        List<LeagueEntity> f2f3 = await context.Leagues
            .Where(e => e.Kind == (int)LeagueKind.Feeder && e.FeederDivision != (int)FeederDivision.First)
            .ToListAsync().ConfigureAwait(false);
        f2f3.Count.ShouldBe(16);

        HashSet<int> f2f3Ids = f2f3.Select(l => l.Id).ToHashSet();
        List<SeasonMembershipEntity> toPool = await context.SeasonMemberships
            .Where(e => e.LeagueId != null && f2f3Ids.Contains(e.LeagueId.Value))
            .ToListAsync().ConfigureAwait(false);
        toPool.Count.ShouldBe(512);
        foreach (SeasonMembershipEntity membership in toPool)
        {
            membership.LeagueId = null;
        }

        context.Leagues.RemoveRange(f2f3);

        RulesV1 v1 = RulesV1.CreateDefault();
        string json = RulesSnapshotCodec.Encode(v1);
        RulesSnapshotEntity snapshot = await context.RulesSnapshots.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        snapshot.RulesVersion = v1.Version;
        snapshot.RulesJson = json;

        await context.SaveChangesAsync().ConfigureAwait(false);
        return saveId;
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

    private static async Task DriveToCupCompleteAsync(SaveStore store, Guid saveId)
    {
        AdvanceToNextEventHandler advance = new(store);
        for (int i = 0; i < 5; i++)
        {
            var response = await advance.HandleAsync(saveId).ConfigureAwait(false);
            if (string.Equals(response.ExecutedAction, "StartNextSeason", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Should not start next season before upgrade.");
            }
        }
    }

    private static async Task<HistorySnapshot> CaptureHistoryAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int stages = await context.StageStandings.CountAsync().ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync().ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync().ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync().ConfigureAwait(false);
        int qualifierStandings = await context.QualifierStandings.CountAsync().ConfigureAwait(false);
        int movements = await context.Movements.CountAsync().ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        RulesSnapshotEntity rules = await context.RulesSnapshots.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return new HistorySnapshot(stages, seasons, rounds, qualifierRounds, qualifierStandings, movements, (ulong)rng.State, rules.RulesJson);
        }
    }

    private static async Task<HashSet<int>> LoadNextF1Async(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await context.SaveMetadata.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == metadata.CurrentSeason + 1).ConfigureAwait(false);
        List<int> f1LeagueIds = await context.Leagues
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .Select(e => e.Id)
            .ToListAsync().ConfigureAwait(false);
        List<int> athleteIds = await context.SeasonMemberships
            .Where(e => e.SeasonId == next.Id && e.LeagueId != null && f1LeagueIds.Contains(e.LeagueId.Value))
            .Select(e => e.SaveAthleteId)
            .ToListAsync().ConfigureAwait(false);
        return athleteIds.ToHashSet();
    }

    private static async Task AssertUpgradedAsync(
        SaveStore store, Guid saveId, UpgradeToTieredResponse response, HashSet<int> f1Before)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await context.SaveMetadata.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == metadata.CurrentSeason + 1).ConfigureAwait(false);

        List<LeagueEntity> feeders = await context.Leagues
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder)
            .ToListAsync().ConfigureAwait(false);
        feeders.Count.ShouldBe(24);

        await AssertPerColorCountsAsync(context, next).ConfigureAwait(false);
        await AssertF1PreservedAsync(context, next, feeders, f1Before).ConfigureAwait(false);
        AssertNoDuplicates(response, f1Before);
    }

    private static async Task AssertPerColorCountsAsync(SaveDbContext context, SeasonEntity next)
    {
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            int pool = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == null && e.SportingColor == (int)color).ConfigureAwait(false);
            pool.ShouldBe(156);
        }
    }

    private static async Task AssertF1PreservedAsync(
        SaveDbContext context, SeasonEntity next, List<LeagueEntity> feeders, HashSet<int> f1Before)
    {
        List<int> f1LeagueIds = feeders
            .Where(l => l.FeederDivision == (int)FeederDivision.First)
            .Select(l => l.Id)
            .ToList();
        HashSet<int> f1After = (await context.SeasonMemberships
            .Where(e => e.SeasonId == next.Id && e.LeagueId != null && f1LeagueIds.Contains(e.LeagueId.Value))
            .Select(e => e.SaveAthleteId)
            .ToListAsync().ConfigureAwait(false)).ToHashSet();
        f1After.SetEquals(f1Before).ShouldBeTrue();
    }

    private static void AssertNoDuplicates(UpgradeToTieredResponse response, HashSet<int> f1Before)
    {
        HashSet<int> f2f3 = response.F2Seeds.Select(s => s.SaveAthleteId)
            .Concat(response.F3Seeds.Select(s => s.SaveAthleteId)).ToHashSet();
        f2f3.Intersect(f1Before).ShouldBeEmpty();
        f2f3.Count.ShouldBe(512);
    }

    private static async Task AssertHistoryPreservedAsync(
        SaveStore store, Guid saveId, HistorySnapshot before, UpgradeToTieredResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        (await context.StageStandings.CountAsync().ConfigureAwait(false)).ShouldBe(before.Stages);
        (await context.SeasonStandings.CountAsync().ConfigureAwait(false)).ShouldBe(before.Seasons);
        (await context.Rounds.CountAsync().ConfigureAwait(false)).ShouldBe(before.Rounds);
        (await context.Movements.CountAsync().ConfigureAwait(false)).ShouldBe(before.Movements + 512);

        RulesSnapshotEntity rulesAfter = await context.RulesSnapshots.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        rulesAfter.RulesVersion.ShouldBe(RulesV2.RulesVersion);
        string.Equals(rulesAfter.RulesJson, before.Rules, StringComparison.Ordinal).ShouldBeFalse();

        RngStateEntity rngAfter = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            ((ulong)rngAfter.State).ShouldBe(response.RngAfterState);
        }
    }

    private static async Task AssertIdempotentAsync(SaveStore store, Guid saveId, UpgradeToTieredResponse first)
    {
        UpgradeToTieredHandler handler = new(store);
        UpgradeToTieredResponse second = await handler.HandleAsync(saveId).ConfigureAwait(false);
        second.AlreadyApplied.ShouldBeTrue();
        second.Checksum.ShouldBe(first.Checksum);
        second.RngAfterState.ShouldBe(first.RngAfterState);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-upgrade-" + Guid.NewGuid().ToString("N"));
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
