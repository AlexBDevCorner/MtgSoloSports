using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Athletes.Projections;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Athletes;

public sealed class AthleteCareerProjectionTests
{
    [Fact]
    public async Task AfterTwoGlobalStages_MatchesStandings()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Career Two Stages", 4242UL, 8484UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            CompleteStageForAllLeaguesHandler bulk = new(store);
            await bulk.HandleAsync(saveId);
            await bulk.HandleAsync(saveId);

            using SaveDbContext context = store.OpenDbContext(saveId);
            int athleteId = await FirstActiveAthleteAsync(context);
            await AssertActiveAfterTwoStagesAsync(context, athleteId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AfterTwoGlobalStages_TracksPoolInactivity()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Career Pool", 4242UL, 8484UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            CompleteStageForAllLeaguesHandler bulk = new(store);
            await bulk.HandleAsync(saveId);
            await bulk.HandleAsync(saveId);

            using SaveDbContext context = store.OpenDbContext(saveId);
            int poolId = await FirstPoolAthleteAsync(context);
            await AssertPoolInactiveAsync(context, poolId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AfterFullSeason_PersistsFinalRanks()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Career Full", 777UL, 888UL, UniverseTestCatalog.Build());
            CompleteStageForAllLeaguesHandler bulk = new(store);
            CompleteStageForAllLeaguesResponse last = null!;
            for (int stage = 1; stage <= 32; stage++)
            {
                last = await bulk.HandleAsync(created.Detail.SaveId);
            }

            last.IsSeasonComplete.ShouldBeTrue();
            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            await AssertChampionFinalizedAsync(context);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AfterFullSeason_PoolStaysInactiveWithNextSeasonEffectiveZero()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Career Pool Full", 777UL, 888UL, UniverseTestCatalog.Build());
            CompleteStageForAllLeaguesHandler bulk = new(store);
            for (int stage = 1; stage <= 32; stage++)
            {
                await bulk.HandleAsync(created.Detail.SaveId);
            }

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            int poolId = await FirstPoolAthleteAsync(context);
            AthleteCareerEntity pool = await context.AthleteCareers.AsNoTracking().SingleAsync(e => e.SaveAthleteId == poolId);
            pool.SeasonsActive.ShouldBe(0);
            pool.IsActive.ShouldBeFalse();
            pool.RoundWins.ShouldBe(0);
            pool.BestSeasonFinish.ShouldBeNull();
            pool.CurrentEffectiveBonusThousandths.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rebuild_IsIdempotent()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Career Rebuild", 999UL, 111UL, UniverseTestCatalog.Build());
            CompleteStageForAllLeaguesHandler bulk = new(store);
            await bulk.HandleAsync(created.Detail.SaveId);
            await bulk.HandleAsync(created.Detail.SaveId);
            await bulk.HandleAsync(created.Detail.SaveId);

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            int athleteId = await FirstActiveAthleteAsync(context);
            AthleteCareerEntity before = await context.AthleteCareers.AsNoTracking().SingleAsync(e => e.SaveAthleteId == athleteId);
            RulesV1 rules = RulesV1.CreateDefault();
            await AthleteProjectionUpdater.RebuildAthleteAsync(context, athleteId, rules, CancellationToken.None);
            await context.SaveChangesAsync();
            AthleteCareerEntity after = await context.AthleteCareers.AsNoTracking().SingleAsync(e => e.SaveAthleteId == athleteId);
            after.RoundWins.ShouldBe(before.RoundWins);
            after.StageWins.ShouldBe(before.StageWins);
            after.StageSeconds.ShouldBe(before.StageSeconds);
            after.StageThirds.ShouldBe(before.StageThirds);
            after.LifetimeEarnedBonusThousandths.ShouldBe(before.LifetimeEarnedBonusThousandths);
            after.CurrentEffectiveBonusThousandths.ShouldBe(before.CurrentEffectiveBonusThousandths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<int> FirstActiveAthleteAsync(SaveDbContext context)
    {
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        return await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId != null)
            .OrderBy(e => e.SaveAthleteId)
            .Select(e => e.SaveAthleteId)
            .FirstAsync().ConfigureAwait(false);
    }

    private static async Task<int> FirstPoolAthleteAsync(SaveDbContext context)
    {
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        return await context.SeasonMemberships
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == null)
            .OrderBy(e => e.SaveAthleteId)
            .Select(e => e.SaveAthleteId)
            .FirstAsync().ConfigureAwait(false);
    }

    private static async Task AssertActiveAfterTwoStagesAsync(SaveDbContext context, int athleteId)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        AthleteCareerEntity career = await context.AthleteCareers.AsNoTracking().SingleAsync(e => e.SaveAthleteId == athleteId).ConfigureAwait(false);
        career.SeasonsActive.ShouldBe(1);
        career.IsActive.ShouldBeTrue();
        career.LastStageNumber.ShouldBe(2);
        List<StageStandingEntity> rows = await context.StageStandings.AsNoTracking().Where(e => e.SaveAthleteId == athleteId).ToListAsync().ConfigureAwait(false);
        rows.Count.ShouldBe(2);
        career.RoundWins.ShouldBe(rows.Sum(r => r.RoundWins));
        career.StageWins.ShouldBe(rows.Count(r => r.StageRank == 1));
        career.StageSeconds.ShouldBe(rows.Count(r => r.StageRank == 2));
        career.StageThirds.ShouldBe(rows.Count(r => r.StageRank == 3));
        career.LifetimeEarnedBonusThousandths.ShouldBe(rows.Sum(r => r.EarnedBonusThousandths));
        Dictionary<int, int> numbers = await context.Seasons.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.SeasonNumber).ConfigureAwait(false);
        IReadOnlyList<BonusContribution> contributions = AthleteProjectionCalculator.BuildContributions(rows, numbers);
        int expected = BonusCalculator.EffectiveBonus(contributions, currentSeason: 1, currentStage: 3, rules).Thousandths;
        career.CurrentEffectiveBonusThousandths.ShouldBe(expected);
        AthleteSeasonSummaryEntity summary = await context.AthleteSeasonSummaries.AsNoTracking().SingleAsync(e => e.SeasonId == season.Id && e.SaveAthleteId == athleteId).ConfigureAwait(false);
        summary.WasActive.ShouldBeTrue();
        summary.SeasonRank.ShouldBeNull();
        summary.RoundWins.ShouldBe(career.RoundWins);
    }

    private static async Task AssertPoolInactiveAsync(SaveDbContext context, int poolId)
    {
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        AthleteCareerEntity pool = await context.AthleteCareers.AsNoTracking().SingleAsync(e => e.SaveAthleteId == poolId).ConfigureAwait(false);
        pool.SeasonsActive.ShouldBe(0);
        pool.IsActive.ShouldBeFalse();
        pool.RoundWins.ShouldBe(0);
        pool.LifetimeEarnedBonusThousandths.ShouldBe(0);
        pool.CurrentEffectiveBonusThousandths.ShouldBe(0);
        AthleteSeasonSummaryEntity summary = await context.AthleteSeasonSummaries.AsNoTracking().SingleAsync(e => e.SeasonId == season.Id && e.SaveAthleteId == poolId).ConfigureAwait(false);
        summary.WasActive.ShouldBeFalse();
        summary.SeasonRank.ShouldBeNull();
        summary.RoundWins.ShouldBe(0);
    }

    private static async Task AssertChampionFinalizedAsync(SaveDbContext context)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        int leagueId = await context.Leagues.AsNoTracking().Where(e => e.SeasonId == season.Id).OrderBy(e => e.Id).Select(e => e.Id).FirstAsync().ConfigureAwait(false);
        SeasonStandingEntity final = await context.SeasonStandings.AsNoTracking().Where(e => e.SeasonId == season.Id && e.LeagueId == leagueId).OrderBy(e => e.SeasonRank).FirstAsync().ConfigureAwait(false);
        AthleteCareerEntity career = await context.AthleteCareers.AsNoTracking().SingleAsync(e => e.SaveAthleteId == final.SaveAthleteId).ConfigureAwait(false);
        career.BestSeasonFinish.ShouldBe(1);
        career.BestSeasonNumber.ShouldBe(1);
        career.LastStageNumber.ShouldBe(32);
        List<StageStandingEntity> rows = await context.StageStandings.AsNoTracking().Where(e => e.SaveAthleteId == final.SaveAthleteId).ToListAsync().ConfigureAwait(false);
        rows.Count.ShouldBe(32);
        career.RoundWins.ShouldBe(rows.Sum(r => r.RoundWins));
        Dictionary<int, int> numbers = await context.Seasons.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.SeasonNumber).ConfigureAwait(false);
        IReadOnlyList<BonusContribution> contributions = AthleteProjectionCalculator.BuildContributions(rows, numbers);
        int expected = BonusCalculator.EffectiveBonus(contributions, currentSeason: 2, currentStage: 1, rules).Thousandths;
        career.CurrentEffectiveBonusThousandths.ShouldBe(expected);
        AthleteSeasonSummaryEntity summary = await context.AthleteSeasonSummaries.AsNoTracking().SingleAsync(e => e.SeasonId == season.Id && e.SaveAthleteId == final.SaveAthleteId).ConfigureAwait(false);
        summary.SeasonRank.ShouldBe(1);
        summary.IsChampion.ShouldBeTrue();
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-athlete-" + Guid.NewGuid().ToString("N"));
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
