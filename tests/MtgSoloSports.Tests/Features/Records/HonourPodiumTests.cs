using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Athletes.GetProfile;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.GetRecords;
using MtgSoloSports.Features.Records.ListHonours;
using MtgSoloSports.Features.Simulation.CompleteSeason;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Records;

/// <summary>
/// MSS-047: competition podiums (1st/2nd/3rd) each contribute exactly one honour;
/// 4th or lower contributes none. Wins/titles/championships stay win-only.
/// </summary>
public sealed class HonourPodiumTests
{
    [Fact]
    public void Mapper_PodiumRanks_MapToDistinctHonourKinds()
    {
        HonourKindMapper.FromLeagueRank(0, 1).ShouldBe(HonourKind.FeederTitle);
        HonourKindMapper.FromLeagueRank(0, 2).ShouldBe(HonourKind.FeederRunnerUp);
        HonourKindMapper.FromLeagueRank(0, 3).ShouldBe(HonourKind.FeederThirdPlace);
        HonourKindMapper.FromLeagueRank((int)LeagueKind.Superleague, 1).ShouldBe(HonourKind.SuperleagueTitle);
        HonourKindMapper.FromLeagueRank((int)LeagueKind.Superleague, 2).ShouldBe(HonourKind.SuperleagueRunnerUp);
        HonourKindMapper.FromLeagueRank((int)LeagueKind.Superleague, 3).ShouldBe(HonourKind.SuperleagueThirdPlace);
        HonourKindMapper.FromColorCupIndividualRank(1).ShouldBe(HonourKind.ColorCupIndividualChampion);
        HonourKindMapper.FromColorCupIndividualRank(2).ShouldBe(HonourKind.ColorCupIndividualRunnerUp);
        HonourKindMapper.FromColorCupIndividualRank(3).ShouldBe(HonourKind.ColorCupIndividualThirdPlace);
        HonourKindMapper.FromColorCupTeamRank(1).ShouldBe(HonourKind.ColorCupTeamChampion);
        HonourKindMapper.FromColorCupTeamRank(2).ShouldBe(HonourKind.ColorCupTeamRunnerUp);
        HonourKindMapper.FromColorCupTeamRank(3).ShouldBe(HonourKind.ColorCupTeamThirdPlace);
        HonourKindMapper.FromTypeCupTeamRank(1).ShouldBe(HonourKind.TypeCupTeamChampion);
        HonourKindMapper.FromTypeCupTeamRank(2).ShouldBe(HonourKind.TypeCupTeamRunnerUp);
        HonourKindMapper.FromTypeCupTeamRank(3).ShouldBe(HonourKind.TypeCupTeamThirdPlace);
    }

    [Fact]
    public void Mapper_RankFour_IsNotAPodiumHonour()
    {
        HonourKindMapper.IsPodiumRank(1).ShouldBeTrue();
        HonourKindMapper.IsPodiumRank(2).ShouldBeTrue();
        HonourKindMapper.IsPodiumRank(3).ShouldBeTrue();
        HonourKindMapper.IsPodiumRank(4).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => HonourKindMapper.FromLeagueRank(0, 4));
        Should.Throw<ArgumentOutOfRangeException>(() => HonourKindMapper.FromColorCupIndividualRank(4));
        Should.Throw<ArgumentOutOfRangeException>(() => HonourKindMapper.FromColorCupTeamRank(4));
        Should.Throw<ArgumentOutOfRangeException>(() => HonourKindMapper.FromTypeCupTeamRank(4));
    }

    [Fact]
    public void ChampionKinds_RemainWinOnly()
    {
        HonourKindMapper.IsChampionKind((int)HonourKind.FeederTitle).ShouldBeTrue();
        HonourKindMapper.IsChampionKind((int)HonourKind.SuperleagueTitle).ShouldBeTrue();
        HonourKindMapper.IsChampionKind((int)HonourKind.ColorCupIndividualChampion).ShouldBeTrue();
        HonourKindMapper.IsChampionKind((int)HonourKind.ColorCupTeamChampion).ShouldBeTrue();
        HonourKindMapper.IsChampionKind((int)HonourKind.TypeCupTeamChampion).ShouldBeTrue();
        HonourKindMapper.IsChampionKind((int)HonourKind.FeederRunnerUp).ShouldBeFalse();
        HonourKindMapper.IsChampionKind((int)HonourKind.FeederThirdPlace).ShouldBeFalse();
        HonourKindMapper.IsChampionKind((int)HonourKind.SuperleagueRunnerUp).ShouldBeFalse();
        HonourKindMapper.IsChampionKind((int)HonourKind.SuperleagueThirdPlace).ShouldBeFalse();
        HonourKindMapper.IsChampionKind((int)HonourKind.ColorCupIndividualRunnerUp).ShouldBeFalse();
        HonourKindMapper.IsChampionKind((int)HonourKind.ColorCupIndividualThirdPlace).ShouldBeFalse();
        HonourKindMapper.IsChampionKind((int)HonourKind.ColorCupTeamRunnerUp).ShouldBeFalse();
        HonourKindMapper.IsChampionKind((int)HonourKind.ColorCupTeamThirdPlace).ShouldBeFalse();
        HonourKindMapper.IsChampionKind((int)HonourKind.TypeCupTeamRunnerUp).ShouldBeFalse();
        HonourKindMapper.IsChampionKind((int)HonourKind.TypeCupTeamThirdPlace).ShouldBeFalse();
    }

    [Fact]
    public async Task AfterFullSeason_PodiumsEachContributeExactlyOneHonour()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Podium Season", 424201UL, 848402UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(saveId);

            await AssertPodiumMappingAsync(store, saveId);
            await AssertFourthGetsNoneAsync(store, saveId);
            await AssertTitlesStayWinOnlyAsync(store, saveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertPodiumMappingAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<HonourEntity> honours = await context.Honours.AsNoTracking().ToListAsync().ConfigureAwait(false);
        honours.Count.ShouldBe(24);

        List<SeasonStandingEntity> podiums = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonRank >= 1 && e.SeasonRank <= 3)
            .ToListAsync().ConfigureAwait(false);
        podiums.Count.ShouldBe(24);

        foreach (SeasonStandingEntity podium in podiums)
        {
            LeagueEntity league = await context.Leagues.AsNoTracking().SingleAsync(e => e.Id == podium.LeagueId).ConfigureAwait(false);
            HonourKind expected = HonourKindMapper.FromLeagueRank(league.Kind, podium.SeasonRank);
            honours.Count(h => h.SeasonId == podium.SeasonId && h.LeagueId == podium.LeagueId && h.Kind == (int)expected && h.SaveAthleteId == podium.SaveAthleteId)
                .ShouldBe(1);
        }
    }

    private static async Task AssertFourthGetsNoneAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<HonourEntity> honours = await context.Honours.AsNoTracking().ToListAsync().ConfigureAwait(false);
        List<SeasonStandingEntity> fourths = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonRank == 4)
            .ToListAsync().ConfigureAwait(false);
        fourths.Count.ShouldBe(8);
        foreach (SeasonStandingEntity fourth in fourths)
        {
            honours.Any(h => h.SeasonId == fourth.SeasonId && h.LeagueId == fourth.LeagueId && h.SaveAthleteId == fourth.SaveAthleteId)
                .ShouldBeFalse();
        }
    }

    private static async Task AssertTitlesStayWinOnlyAsync(SaveStore store, Guid saveId)
    {
        GetRecordsHandler recordsHandler = new(store);
        GetRecordsResponse records = await recordsHandler.HandleAsync(saveId).ConfigureAwait(false);
        RecordEntry feeder = records.Records.Single(r => string.Equals(r.RecordKey, RecordKey.FeederTitles, StringComparison.Ordinal));
        feeder.Value.ShouldBe(1);
        feeder.Holders.Count.ShouldBe(8);
    }

    [Fact]
    public async Task HistoricalPodiums_InterpretedWithoutRecreatingCompetitions()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Podium Historical", 777001UL, 888002UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(saveId);
            await DeletePodiumRowsAsync(store, saveId);
            await AssertFallbackListsPodiumsAsync(store, saveId);
            await AssertProfileInterpretsRunnerUpAsync(store, saveId);
            await AssertFourthResultContributesNoneAsync(store, saveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task DeletePodiumRowsAsync(SaveStore store, Guid saveId)
    {
        int runnerUpKind = (int)HonourKind.FeederRunnerUp;
        int thirdKind = (int)HonourKind.FeederThirdPlace;
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<HonourEntity> podiumRows = await context.Honours
            .Where(e => e.Kind == runnerUpKind || e.Kind == thirdKind)
            .ToListAsync().ConfigureAwait(false);
        podiumRows.Count.ShouldBe(16);
        context.Honours.RemoveRange(podiumRows);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task AssertFallbackListsPodiumsAsync(SaveStore store, Guid saveId)
    {
        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            (await context.Honours.CountAsync().ConfigureAwait(false)).ShouldBe(8);
        }

        ListHonoursHandler listHandler = new(store);
        ListHonoursResponse listed = await listHandler.HandleAsync(saveId).ConfigureAwait(false);
        listed.Honours.Count.ShouldBe(24);
        listed.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.FeederRunnerUp), StringComparison.Ordinal)).ShouldBe(8);
        listed.Honours.Count(h => string.Equals(h.HonourKind, nameof(HonourKind.FeederThirdPlace), StringComparison.Ordinal)).ShouldBe(8);
    }

    private static async Task AssertProfileInterpretsRunnerUpAsync(SaveStore store, Guid saveId)
    {
        int runnerUpAthlete;
        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            SeasonStandingEntity runnerUp = await context.SeasonStandings
                .AsNoTracking()
                .Where(e => e.SeasonRank == 2)
                .OrderBy(e => e.SeasonId)
                .ThenBy(e => e.LeagueId)
                .FirstAsync().ConfigureAwait(false);
            runnerUpAthlete = runnerUp.SaveAthleteId;
        }

        GetAthleteProfileHandler profiles = new(store);
        GetAthleteProfileResponse profile = await profiles.HandleAsync(saveId, runnerUpAthlete).ConfigureAwait(false);
        profile.Honours.Any(h => string.Equals(h.HonourKind, nameof(HonourKind.FeederRunnerUp), StringComparison.Ordinal)).ShouldBeTrue();
    }

    private static async Task AssertFourthResultContributesNoneAsync(SaveStore store, Guid saveId)
    {
        int fourthAthlete;
        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            SeasonStandingEntity fourth = await context.SeasonStandings
                .AsNoTracking()
                .Where(e => e.SeasonRank == 4)
                .OrderBy(e => e.SeasonId)
                .ThenBy(e => e.LeagueId)
                .FirstAsync().ConfigureAwait(false);
            fourthAthlete = fourth.SaveAthleteId;
        }

        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            SeasonStandingEntity fourth = await context.SeasonStandings
                .AsNoTracking()
                .Where(e => e.SaveAthleteId == fourthAthlete && e.SeasonRank == 4)
                .FirstAsync().ConfigureAwait(false);
            LeagueEntity league = await context.Leagues.AsNoTracking().SingleAsync(e => e.Id == fourth.LeagueId).ConfigureAwait(false);
            SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.Id == fourth.SeasonId).ConfigureAwait(false);
            GetAthleteProfileHandler profiles = new(store);
            GetAthleteProfileResponse fourthProfile = await profiles.HandleAsync(saveId, fourthAthlete).ConfigureAwait(false);
            fourthProfile.Honours.Any(h => h.SeasonNumber == season.SeasonNumber && string.Equals(h.LeagueName, league.Name, StringComparison.Ordinal)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task MultiplePodiums_AccumulateWithoutDuplication()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Podium Accumulate", 111003UL, 222004UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(saveId);

            ListHonoursHandler listHandler = new(store);
            ListHonoursResponse response = await listHandler.HandleAsync(saveId);
            List<string> keys = response.Honours
                .Select(h => $"{h.SeasonId}:{h.LeagueId}:{h.HonourKind}:{h.AthleteId}")
                .ToList();
            keys.Distinct(StringComparer.Ordinal).Count().ShouldBe(keys.Count);
            response.Honours.Count.ShouldBe(24);

            int anyAthlete = response.Honours[0].AthleteId;
            ListHonoursResponse filtered = await listHandler.HandleAsync(saveId, anyAthlete);
            filtered.Honours.All(h => h.AthleteId == anyAthlete).ShouldBeTrue();
            List<string> filteredKeys = filtered.Honours
                .Select(h => $"{h.SeasonId}:{h.LeagueId}:{h.HonourKind}")
                .ToList();
            filteredKeys.Distinct(StringComparer.Ordinal).Count().ShouldBe(filteredKeys.Count);

            GetAthleteProfileHandler profiles = new(store);
            GetAthleteProfileResponse profile = await profiles.HandleAsync(saveId, anyAthlete);
            profile.Honours.Count.ShouldBe(filtered.Honours.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-podium-" + Guid.NewGuid().ToString("N"));
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
