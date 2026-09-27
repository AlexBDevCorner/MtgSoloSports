using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Features.Seasons.AdvanceToNextEvent;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Simulation.CompleteSeason;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Simulation;

public sealed class FastSimulationTests
{
    [Fact]
    public async Task CompleteSeason_EquivalentToSequentialBulkStages()
    {
        var (firstStore, firstRoot) = CreateStore();
        var (secondStore, secondRoot) = CreateStore();
        try
        {
            var catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord sequential = await firstStore.CreateAsync("Season Sequential", 5150UL, 6160UL, catalog);
            SaveStore.CreationRecord fast = await secondStore.CreateAsync("Season Fast", 5150UL, 6160UL, catalog);

            CompleteStageForAllLeaguesHandler bulk = new(firstStore);
            for (int stage = 1; stage <= 32; stage++)
            {
                CompleteStageForAllLeaguesResponse completed = await bulk.HandleAsync(sequential.Detail.SaveId);
                completed.CompletedStage.ShouldBe(stage);
            }

            CompleteSeasonHandler fastHandler = new(secondStore);
            CompleteSeasonResponse response = await fastHandler.HandleAsync(fast.Detail.SaveId);

            response.SeasonNumber.ShouldBe(1);
            response.StagesCompleted.ShouldBe(32);
            response.GlobalStageBefore.ShouldBe(1);
            response.GlobalStageAfter.ShouldBe(33);
            response.IsSeasonComplete.ShouldBeTrue();
            response.Progress.StagesCompleted.ShouldBe(32);
            response.Progress.TotalStagesInSeason.ShouldBe(32);
            response.Progress.GlobalStageBefore.ShouldBe(1);
            response.Progress.GlobalStageAfter.ShouldBe(33);

            await AssertSportingEquivalentAsync(firstStore, sequential.Detail.SaveId, secondStore, fast.Detail.SaveId);
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteSeason_PartialProgress_CompletesRemainder()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Season Partial", 7171UL, 8181UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;

            CompleteStageForAllLeaguesHandler bulk = new(store);
            for (int stage = 1; stage <= 5; stage++)
            {
                await bulk.HandleAsync(saveId);
            }

            CompleteSeasonHandler fast = new(store);
            CompleteSeasonResponse response = await fast.HandleAsync(saveId);

            response.SeasonNumber.ShouldBe(1);
            response.StagesCompleted.ShouldBe(27);
            response.GlobalStageBefore.ShouldBe(6);
            response.GlobalStageAfter.ShouldBe(33);
            response.IsSeasonComplete.ShouldBeTrue();
            response.Progress.StagesCompleted.ShouldBe(27);

            GetSeasonStatusHandler status = new(store);
            GetSeasonStatusResponse after = await status.HandleAsync(saveId);
            after.IsCurrentSeasonComplete.ShouldBeTrue();
            after.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.SeasonComplete));
            after.LegalNextActions.ShouldBe([SeasonLifecycleActions.ResolveInauguralMovement]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteSeason_AlreadyComplete_ThrowsConflict()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Season Done", 9191UL, 9202UL, UniverseTestCatalog.Build());
            CompleteSeasonHandler fast = new(store);
            await fast.HandleAsync(created.Detail.SaveId);
            await Should.ThrowAsync<CompleteSeasonConflictException>(() => fast.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SimulateSeasons_EquivalentToManualAdvanceToNextEvent()
    {
        var (firstStore, firstRoot) = CreateStore();
        var (secondStore, secondRoot) = CreateStore();
        try
        {
            var catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord manual = await firstStore.CreateAsync("Sim Manual", 4243UL, 778UL, catalog);
            SaveStore.CreationRecord fast = await secondStore.CreateAsync("Sim Fast", 4243UL, 778UL, catalog);

            await AdvanceManuallyToSeasonTwoAsync(firstStore, manual.Detail.SaveId);

            SimulateSeasonsHandler fastHandler = new(secondStore);
            SimulateSeasonsResponse response = await fastHandler.HandleAsync(
                fast.Detail.SaveId, new SimulateSeasonsRequest(1));

            response.SeasonsRequested.ShouldBe(1);
            response.SeasonsCompleted.ShouldBe(1);
            response.StagesCompleted.ShouldBe(32);
            response.StartSeasonNumber.ShouldBe(1);
            response.EndSeasonNumber.ShouldBe(2);
            response.ComputedPhase.ShouldBe(SavePhaseParser.ToText(SavePhase.SeasonInProgress));
            response.GlobalStage.ShouldBe(1);
            response.IsCurrentSeasonComplete.ShouldBeFalse();
            response.Progress.SeasonsRequested.ShouldBe(1);
            response.Progress.SeasonsCompleted.ShouldBe(1);
            response.Progress.StartSeasonNumber.ShouldBe(1);
            response.Progress.EndSeasonNumber.ShouldBe(2);

            await AssertSportingEquivalentAsync(firstStore, manual.Detail.SaveId, secondStore, fast.Detail.SaveId);
            await AssertPostseasonEquivalentAsync(firstStore, manual.Detail.SaveId, secondStore, fast.Detail.SaveId, fromSeason: 1, toSeason: 2);
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SimulateSeasons_FromMidSeason_EquivalentToManual()
    {
        var (firstStore, firstRoot) = CreateStore();
        var (secondStore, secondRoot) = CreateStore();
        try
        {
            var catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord manual = await firstStore.CreateAsync("Sim Mid Manual", 3331UL, 4442UL, catalog);
            SaveStore.CreationRecord fast = await secondStore.CreateAsync("Sim Mid Fast", 3331UL, 4442UL, catalog);

            // Advance both saves identically through 5 global stages first.
            CompleteStageForAllLeaguesHandler primer = new(firstStore);
            CompleteStageForAllLeaguesHandler primer2 = new(secondStore);
            for (int stage = 1; stage <= 5; stage++)
            {
                await primer.HandleAsync(manual.Detail.SaveId);
                await primer2.HandleAsync(fast.Detail.SaveId);
            }

            await AdvanceManuallyToSeasonTwoAsync(firstStore, manual.Detail.SaveId);

            SimulateSeasonsHandler fastHandler = new(secondStore);
            SimulateSeasonsResponse response = await fastHandler.HandleAsync(
                fast.Detail.SaveId, new SimulateSeasonsRequest(1));

            response.StartSeasonNumber.ShouldBe(1);
            response.EndSeasonNumber.ShouldBe(2);
            response.StagesCompleted.ShouldBe(27);
            response.SeasonsCompleted.ShouldBe(1);

            await AssertSportingEquivalentAsync(firstStore, manual.Detail.SaveId, secondStore, fast.Detail.SaveId);
        }
        finally
        {
            Directory.Delete(firstRoot, recursive: true);
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    [InlineData(1000)]
    public async Task SimulateSeasons_OutOfRange_ThrowsArgument(int seasons)
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Sim Bounds", 111UL, 222UL, UniverseTestCatalog.Build());
            SimulateSeasonsHandler handler = new(store);
            await Should.ThrowAsync<ArgumentException>(() =>
                handler.HandleAsync(created.Detail.SaveId, new SimulateSeasonsRequest(seasons)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SimulateSeasons_BoundPreservesHundredsOfSeasons()
    {
        SimulateSeasonsHandler.MaxSeasonsPerRequest.ShouldBeGreaterThanOrEqualTo(200);
        SimulateSeasonsHandler.ValidateSeasons(1);
        SimulateSeasonsHandler.ValidateSeasons(SimulateSeasonsHandler.MaxSeasonsPerRequest);
    }

    [Fact]
    public async Task SimulateSeasons_NullRequest_ThrowsArgument()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Sim Null", 555UL, 666UL, UniverseTestCatalog.Build());
            SimulateSeasonsHandler handler = new(store);
            await Should.ThrowAsync<ArgumentException>(() =>
                handler.HandleAsync(created.Detail.SaveId, null!));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AdvanceManuallyToSeasonTwoAsync(SaveStore store, Guid saveId)
    {
        AdvanceToNextEventHandler stepper = new(store);
        GetSeasonStatusHandler manualStatus = new(store);
        int guard = 0;
        while (true)
        {
            guard = checked(guard + 1);
            if (guard > 60)
            {
                throw new InvalidOperationException("Manual lifecycle did not reach Season 2.");
            }

            GetSeasonStatusResponse before = await manualStatus.HandleAsync(saveId).ConfigureAwait(false);
            if (before.CurrentSeasonNumber == 2 && string.Equals(before.ComputedPhase, SavePhaseParser.ToText(SavePhase.SeasonInProgress), StringComparison.Ordinal))
            {
                break;
            }

            await stepper.HandleAsync(saveId).ConfigureAwait(false);
        }
    }

    private static async Task AssertSportingEquivalentAsync(
        SaveStore firstStore, Guid firstSave, SaveStore secondStore, Guid secondSave)
    {
        (ulong firstState, ulong firstStream) = await LoadRngAsync(firstStore, firstSave).ConfigureAwait(false);
        (ulong secondState, ulong secondStream) = await LoadRngAsync(secondStore, secondSave).ConfigureAwait(false);
        firstState.ShouldBe(secondState);
        firstStream.ShouldBe(secondStream);

        List<string> firstRounds = await LoadRoundChecksumsAsync(firstStore, firstSave).ConfigureAwait(false);
        List<string> secondRounds = await LoadRoundChecksumsAsync(secondStore, secondSave).ConfigureAwait(false);
        firstRounds.ShouldBe(secondRounds);

        List<string> firstStages = await LoadStageFingerprintsAsync(firstStore, firstSave).ConfigureAwait(false);
        List<string> secondStages = await LoadStageFingerprintsAsync(secondStore, secondSave).ConfigureAwait(false);
        firstStages.ShouldBe(secondStages);

        List<string> firstSeasons = await LoadSeasonFingerprintsAsync(firstStore, firstSave).ConfigureAwait(false);
        List<string> secondSeasons = await LoadSeasonFingerprintsAsync(secondStore, secondSave).ConfigureAwait(false);
        firstSeasons.ShouldBe(secondSeasons);
    }

    private static async Task AssertPostseasonEquivalentAsync(
        SaveStore firstStore, Guid firstSave, SaveStore secondStore, Guid secondSave, int fromSeason, int toSeason)
    {
        List<string> firstMemberships = await LoadMembershipFingerprintsAsync(firstStore, firstSave, toSeason).ConfigureAwait(false);
        List<string> secondMemberships = await LoadMembershipFingerprintsAsync(secondStore, secondSave, toSeason).ConfigureAwait(false);
        firstMemberships.ShouldBe(secondMemberships);

        int firstMovements = await LoadMovementCountAsync(firstStore, firstSave, fromSeason, toSeason).ConfigureAwait(false);
        int secondMovements = await LoadMovementCountAsync(secondStore, secondSave, fromSeason, toSeason).ConfigureAwait(false);
        firstMovements.ShouldBe(secondMovements);
        firstMovements.ShouldBeGreaterThan(0);
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

    private static async Task<List<string>> LoadRoundChecksumsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Rounds.AsNoTracking()
            .OrderBy(e => e.SeasonId)
            .ThenBy(e => e.LeagueId)
            .ThenBy(e => e.StageNumber)
            .ThenBy(e => e.RoundNumber)
            .Select(e => e.SeasonId + ":" + e.LeagueId + ":" + e.StageNumber + ":" + e.RoundNumber + ":" + e.PayloadChecksum)
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task<List<string>> LoadStageFingerprintsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.StageStandings.AsNoTracking()
            .OrderBy(e => e.SeasonId)
            .ThenBy(e => e.LeagueId)
            .ThenBy(e => e.StageNumber)
            .ThenBy(e => e.StageRank)
            .Select(e => e.SeasonId + ":" + e.LeagueId + ":" + e.StageNumber + ":" + e.StageRank + ":" + e.SaveAthleteId + ":" + e.StageScoreThousandths + ":" + e.ChampionshipPointsThousandths + ":" + e.EarnedBonusThousandths)
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task<List<string>> LoadSeasonFingerprintsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SeasonStandings.AsNoTracking()
            .OrderBy(e => e.SeasonId)
            .ThenBy(e => e.LeagueId)
            .ThenBy(e => e.SeasonRank)
            .Select(e => e.SeasonId + ":" + e.LeagueId + ":" + e.SeasonRank + ":" + e.SaveAthleteId + ":" + e.TotalChampionshipPointsThousandths + ":" + e.TotalStageScoreThousandths)
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task<List<string>> LoadMembershipFingerprintsAsync(SaveStore store, Guid saveId, int seasonNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        return await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == season.Id)
            .OrderBy(e => e.SaveAthleteId)
            .Select(e => e.SaveAthleteId + ":" + (e.LeagueId == null ? "pool" : e.LeagueId.Value.ToString(CultureInfo.InvariantCulture)))
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task<int> LoadMovementCountAsync(SaveStore store, Guid saveId, int fromSeason, int toSeason)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == fromSeason).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == toSeason).ConfigureAwait(false);
        return await context.Movements.CountAsync(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id).ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-fast-" + Guid.NewGuid().ToString("N"));
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
