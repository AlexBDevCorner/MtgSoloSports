using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.GetColorCupSelection;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.ListHonours;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Stories;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class ColorCupIndividualTests
{
    [Fact]
    public async Task Run_BeforeSelection_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Early", 111UL, 222UL, UniverseTestCatalog.Build());
            RunColorCupIndividualHandler handler = new(store);
            await Should.ThrowAsync<RunColorCupIndividualConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_EvenSeason_Conflicts()
    {
        // MSS-067: shared Color Cup template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup-ind-");
        try
        {
            await MarkSeasonTwoCompleteAsync(store, saveId);

            RunColorCupIndividualHandler handler = new(store);
            await Should.ThrowAsync<RunColorCupIndividualConflictException>(
                () => handler.HandleAsync(saveId, sourceSeasonNumber: 2));
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
            SaveStore.CreationRecord created = await store.CreateAsync("Cup Missing", 505UL, 606UL, UniverseTestCatalog.Build());
            GetColorCupIndividualResultHandler query = new(store);
            await Should.ThrowAsync<ColorCupIndividualResultNotFoundException>(
                () => query.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_AfterSelection_Produces32And16WithMedalsHonourAndPreservesHistory()
    {
        // MSS-067: shared Color Cup template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup-ind-");
        try
        {
            GetColorCupSelectionResponse selection = await new GetColorCupSelectionHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            selection.TotalSelected.ShouldBe(32);

            (int stages, int seasons, int rounds, long lifetime, long effective, long championship, ulong rng) =
                await CapturePreservationAsync(store, saveId);

            RunColorCupIndividualHandler handler = new(store);
            RunColorCupIndividualResponse response = await handler.HandleAsync(saveId);

            response.SourceSeasonNumber.ShouldBe(1);
            response.CupSize.ShouldBe(32);
            response.Rounds.ShouldBe(16);
            response.Standings.Count.ShouldBe(32);
            response.Standings.Select(m => m.CupRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, 32).ToList());
            response.Checksum.ShouldNotBeNullOrWhiteSpace();

            HashSet<int> selectedIds = selection.Teams.SelectMany(t => t.Members).Select(m => m.SaveAthleteId).ToHashSet();
            HashSet<int> cupIds = response.Standings.Select(m => m.AthleteId).ToHashSet();
            cupIds.SetEquals(selectedIds).ShouldBeTrue();

            ColorCupIndividualMember gold = response.Standings.Single(m => m.CupRank == 1);
            ColorCupIndividualMember silver = response.Standings.Single(m => m.CupRank == 2);
            ColorCupIndividualMember bronze = response.Standings.Single(m => m.CupRank == 3);
            gold.Medal.ShouldBe(nameof(ColorCupMedal.Gold));
            silver.Medal.ShouldBe(nameof(ColorCupMedal.Silver));
            bronze.Medal.ShouldBe(nameof(ColorCupMedal.Bronze));
            response.Standings.Where(m => m.CupRank > 3).All(m => string.Equals(m.Medal, nameof(ColorCupMedal.None), StringComparison.Ordinal)).ShouldBeTrue();
            response.ChampionAthleteId.ShouldBe(gold.AthleteId);
            response.ChampionName.ShouldBe(gold.Name);

            await AssertPersistedAsync(store, saveId, response);
            await AssertHonourAsync(store, saveId, response);
            await AssertStoriesAsync(store, saveId, response);
            await AssertPreservationAsync(store, saveId, stages, seasons, rounds, lifetime, effective, championship, rng);
            await AssertQueryMatchesAsync(store, saveId, response);
            await AssertReplayAsync(store, saveId, response);

            await Should.ThrowAsync<RunColorCupIndividualConflictException>(
                () => handler.HandleAsync(saveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_ParticipationCannotChangeCareerBonus()
    {
        // MSS-067: shared Color Cup template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup-ind-");
        try
        {

            Dictionary<int, int> lifetimeBefore = await LoadCareerBonusAsync(store, saveId, lifetime: true);
            Dictionary<int, int> effectiveBefore = await LoadCareerBonusAsync(store, saveId, lifetime: false);
            (int stagesBefore, int seasonsBefore, int roundsBefore, long championshipBefore) =
                await CaptureLeagueCountsAsync(store, saveId);

            RunColorCupIndividualHandler handler = new(store);
            RunColorCupIndividualResponse response = await handler.HandleAsync(saveId);
            response.Standings.Count.ShouldBe(32);

            Dictionary<int, int> lifetimeAfter = await LoadCareerBonusAsync(store, saveId, lifetime: true);
            Dictionary<int, int> effectiveAfter = await LoadCareerBonusAsync(store, saveId, lifetime: false);
            lifetimeAfter.ShouldBe(lifetimeBefore);
            effectiveAfter.ShouldBe(effectiveBefore);

            (int stagesAfter, int seasonsAfter, int roundsAfter, long championshipAfter) =
                await CaptureLeagueCountsAsync(store, saveId);
            stagesAfter.ShouldBe(stagesBefore);
            seasonsAfter.ShouldBe(seasonsBefore);
            roundsAfter.ShouldBe(roundsBefore);
            championshipAfter.ShouldBe(championshipBefore);

            await AssertActiveBonusUsedAsync(store, saveId, response);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_SameSeed_IsDeterministic()
    {
        // MSS-067: one prepared save forked into two isolated copies instead of
        // simulating the same Season 1 twice; both runs still execute
        // independently from bit-identical starting state.
        var (store, root, saveId) = await PrepareCupForSeedAsync(4242UL, 777UL);
        var (secondStore, secondRoot, secondId) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-cup-det-");
        try
        {
            RunColorCupIndividualResponse first = await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            RunColorCupIndividualResponse second = await new RunColorCupIndividualHandler(secondStore).HandleAsync(secondId);
            first.Checksum.ShouldBe(second.Checksum);
            first.Standings.Select(m => m.AthleteId).ShouldBe(second.Standings.Select(m => m.AthleteId).ToList());
            first.Standings.Select(m => m.CupScoreThousandths).ShouldBe(second.Standings.Select(m => m.CupScoreThousandths).ToList());
            first.ChampionAthleteId.ShouldBe(second.ChampionAthleteId);
            first.RngBeforeState.ShouldBe(second.RngBeforeState);
            first.RngAfterState.ShouldBe(second.RngAfterState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            TestSaveStores.DeleteRoot(secondRoot);
        }
    }

    private static async Task AssertPersistedAsync(SaveStore store, Guid saveId, RunColorCupIndividualResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);

        List<ColorCupIndividualRoundEntity> rounds = await context.ColorCupIndividualRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.RoundNumber).ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(16);
        rounds.Select(r => r.RoundNumber).ShouldBe(Enumerable.Range(1, 16).ToList());
        foreach (ColorCupIndividualRoundEntity round in rounds)
        {
            round.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();
            ColorCupIndividualRoundPayloadDocument document = ColorCupIndividualRoundPayloadDocument.FromStored(round.PayloadJson);
            document.Placements.Count.ShouldBe(32);
            document.Checksum.ShouldBe(round.PayloadChecksum);
            document.SourceSeasonNumber.ShouldBe(response.SourceSeasonNumber);
        }

        List<ColorCupIndividualStandingEntity> standings = await context.ColorCupIndividualStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.CupRank).ToListAsync().ConfigureAwait(false);
        standings.Count.ShouldBe(32);
        standings.Select(s => s.CupRank).ShouldBe(Enumerable.Range(1, 32).ToList());
        standings.Single(s => s.CupRank == 1).Medal.ShouldBe((int)ColorCupMedal.Gold);
        standings.Single(s => s.CupRank == 2).Medal.ShouldBe((int)ColorCupMedal.Silver);
        standings.Single(s => s.CupRank == 3).Medal.ShouldBe((int)ColorCupMedal.Bronze);
        standings.Where(s => s.CupRank > 3).All(s => s.Medal == (int)ColorCupMedal.None).ShouldBeTrue();
    }

    private static async Task AssertHonourAsync(SaveStore store, Guid saveId, RunColorCupIndividualResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        List<HonourEntity> cupHonours = await context.Honours.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && (e.Kind == (int)HonourKind.ColorCupIndividualChampion
                || e.Kind == (int)HonourKind.ColorCupIndividualRunnerUp
                || e.Kind == (int)HonourKind.ColorCupIndividualThirdPlace))
            .ToListAsync().ConfigureAwait(false);
        // MSS-047: 1st/2nd/3rd each contribute exactly one honour.
        cupHonours.Count.ShouldBe(3);
        List<ColorCupIndividualStandingEntity> standings = await context.ColorCupIndividualStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.CupRank >= 1 && e.CupRank <= 3)
            .OrderBy(e => e.CupRank).ToListAsync().ConfigureAwait(false);
        standings.Count.ShouldBe(3);
        foreach (ColorCupIndividualStandingEntity standing in standings)
        {
            HonourKind expectedKind = standing.CupRank switch
            {
                1 => HonourKind.ColorCupIndividualChampion,
                2 => HonourKind.ColorCupIndividualRunnerUp,
                _ => HonourKind.ColorCupIndividualThirdPlace,
            };
            cupHonours.Single(h => h.Kind == (int)expectedKind).SaveAthleteId.ShouldBe(standing.SaveAthleteId);
        }

        cupHonours.Single(h => h.Kind == (int)HonourKind.ColorCupIndividualChampion).SaveAthleteId.ShouldBe(response.ChampionAthleteId);
        cupHonours.All(h => string.Equals(h.LeagueName, RunColorCupIndividualHandler.CupLeagueName, StringComparison.Ordinal)).ShouldBeTrue();

        // Fourth place contributes no honour.
        int fourthAthlete = (await context.ColorCupIndividualStandings.AsNoTracking()
            .SingleAsync(e => e.SourceSeasonId == source.Id && e.CupRank == 4).ConfigureAwait(false)).SaveAthleteId;
        cupHonours.Any(h => h.SaveAthleteId == fourthAthlete).ShouldBeFalse();

        ListHonoursHandler honoursHandler = new(store);
        ListHonoursResponse honours = await honoursHandler.HandleAsync(saveId).ConfigureAwait(false);
        honours.Honours.Any(h =>
            h.SeasonNumber == response.SourceSeasonNumber &&
            h.AthleteId == response.ChampionAthleteId &&
            string.Equals(h.HonourKind, nameof(HonourKind.ColorCupIndividualChampion), StringComparison.Ordinal)).ShouldBeTrue();
        honours.Honours.Count.ShouldBeGreaterThan(8);
    }

    private static async Task AssertStoriesAsync(SaveStore store, Guid saveId, RunColorCupIndividualResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<StoryEventEntity> stories = await context.StoryEvents.AsNoTracking()
            .Where(e => e.SeasonNumber == response.SourceSeasonNumber &&
                (e.EventType == StoryEventType.ColorCupIndividualTitle || e.EventType == StoryEventType.ColorCupMedal))
            .ToListAsync().ConfigureAwait(false);
        stories.Count(e => string.Equals(e.EventType, StoryEventType.ColorCupMedal, StringComparison.Ordinal)).ShouldBe(3);
        stories.Count(e => string.Equals(e.EventType, StoryEventType.ColorCupIndividualTitle, StringComparison.Ordinal)).ShouldBe(1);
        StoryEventEntity title = stories.Single(e => string.Equals(e.EventType, StoryEventType.ColorCupIndividualTitle, StringComparison.Ordinal));
        title.SaveAthleteId.ShouldBe(response.ChampionAthleteId);
        string text = StoryEventRenderer.Render(title.EventType, title.ContextJson);
        text.ShouldContain(response.ChampionName);
    }

    private static async Task AssertPreservationAsync(
        SaveStore store,
        Guid saveId,
        int stages,
        int seasons,
        int rounds,
        long lifetime,
        long effective,
        long championship,
        ulong rngBefore)
    {
        (int stagesAfter, int seasonsAfter, int roundsAfter, long lifetimeAfter, long effectiveAfter, long championshipAfter, ulong rngAfter) =
            await CapturePreservationAsync(store, saveId).ConfigureAwait(false);
        stagesAfter.ShouldBe(stages);
        seasonsAfter.ShouldBe(seasons);
        roundsAfter.ShouldBe(rounds);
        lifetimeAfter.ShouldBe(lifetime);
        effectiveAfter.ShouldBe(effective);
        championshipAfter.ShouldBe(championship);
        rngAfter.ShouldNotBe(rngBefore);
    }

    private static async Task AssertQueryMatchesAsync(SaveStore store, Guid saveId, RunColorCupIndividualResponse response)
    {
        GetColorCupIndividualResultHandler query = new(store);
        GetColorCupIndividualResultResponse summary = await query.HandleAsync(saveId).ConfigureAwait(false);
        summary.SourceSeasonNumber.ShouldBe(response.SourceSeasonNumber);
        summary.CupSize.ShouldBe(32);
        summary.Rounds.ShouldBe(16);
        summary.Checksum.ShouldNotBeNullOrWhiteSpace();
        summary.ChampionAthleteId.ShouldBe(response.ChampionAthleteId);
        summary.Standings.Count.ShouldBe(32);
        summary.Standings.Select(m => m.AthleteId).ShouldBe(response.Standings.Select(m => m.AthleteId).ToList());
        summary.Standings.Select(m => m.CupRank).ShouldBe(response.Standings.Select(m => m.CupRank).ToList());
        summary.Standings.Select(m => m.Medal).ShouldBe(response.Standings.Select(m => m.Medal).ToList());

        GetColorCupIndividualResultResponse again = await query.HandleAsync(saveId, sourceSeasonNumber: response.SourceSeasonNumber).ConfigureAwait(false);
        again.ChampionAthleteId.ShouldBe(response.ChampionAthleteId);
        again.Rounds.ShouldBe(16);
    }

    private static async Task AssertReplayAsync(SaveStore store, Guid saveId, RunColorCupIndividualResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        List<ColorCupIndividualRoundEntity> rounds = await context.ColorCupIndividualRounds.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id)
            .OrderBy(e => e.RoundNumber).ToListAsync().ConfigureAwait(false);
        ColorCupIndividualRoundPayloadDocument first = ColorCupIndividualRoundPayloadDocument.FromStored(rounds[0].PayloadJson);
        first.Placements.Count.ShouldBe(32);
        first.RngBeforeState.ShouldBe(response.RngBeforeState);
        ColorCupIndividualRoundPayloadDocument last = ColorCupIndividualRoundPayloadDocument.FromStored(rounds[^1].PayloadJson);
        last.RngAfterState.ShouldBe(response.RngAfterState);
        foreach (ColorCupIndividualRoundEntity round in rounds)
        {
            round.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();
            ColorCupIndividualRoundPayloadDocument document = ColorCupIndividualRoundPayloadDocument.FromStored(round.PayloadJson);
            document.Checksum.ShouldBe(round.PayloadChecksum);
        }
    }

    private static async Task AssertActiveBonusUsedAsync(SaveStore store, Guid saveId, RunColorCupIndividualResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.SourceSeasonNumber).ConfigureAwait(false);
        ColorCupIndividualRoundEntity roundOne = await context.ColorCupIndividualRounds.AsNoTracking()
            .SingleAsync(e => e.SourceSeasonId == source.Id && e.RoundNumber == 1).ConfigureAwait(false);
        roundOne.PayloadJson.StartsWith(RoundPayloadCodec.BrotliPrefix, StringComparison.Ordinal).ShouldBeTrue();
        ColorCupIndividualRoundPayloadDocument document = ColorCupIndividualRoundPayloadDocument.FromStored(roundOne.PayloadJson);

        Dictionary<int, int> seasonNumbers = await context.Seasons.AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber).ConfigureAwait(false);
        int cupSeason = source.SeasonNumber + 1;
        List<ColorCupIndividualStandingEntity> standings = await context.ColorCupIndividualStandings.AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        foreach (ColorCupIndividualStandingEntity standing in standings)
        {
            List<StageStandingEntity> rows = await context.StageStandings.AsNoTracking()
                .Where(e => e.SaveAthleteId == standing.SaveAthleteId).ToListAsync().ConfigureAwait(false);
            List<MtgSoloSports.SimulationKernel.Scoring.BonusContribution> contributions = new(rows.Count);
            foreach (StageStandingEntity row in rows)
            {
                int earnedSeason = seasonNumbers[row.SeasonId];
                contributions.Add(new MtgSoloSports.SimulationKernel.Scoring.BonusContribution(
                    earnedSeason,
                    row.StageNumber,
                    MtgSoloSports.SimulationKernel.FixedPoint.Bonus.FromThousandths(row.EarnedBonusThousandths)));
            }

            MtgSoloSports.SimulationKernel.FixedPoint.Bonus expected =
                MtgSoloSports.SimulationKernel.Scoring.BonusCalculator.EffectiveBonus(
                    contributions, cupSeason, 1, MtgSoloSports.SimulationKernel.Rules.RulesV1.CreateDefault());
            int actual = document.Placements.Single(p => p.AthleteId == standing.SaveAthleteId).ActiveBonusThousandths;
            actual.ShouldBe(expected.Thousandths);
        }
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareCupForSeedAsync(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Cup Det", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        await CompleteSeasonOneAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        SelectColorCupTeamsHandler select = new(store);
        await select.HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        return (store, root, created.Detail.SaveId);
    }

    private static async Task<(int StageCount, int SeasonCount, int RoundCount, long Lifetime, long Effective, long Championship, ulong Rng)> CapturePreservationAsync(
        SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int stages = await context.StageStandings.CountAsync().ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync().ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync().ConfigureAwait(false);
        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths).ConfigureAwait(false);
        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths).ConfigureAwait(false);
        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths).ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return (stages, seasons, rounds, lifetime, effective, championship, (ulong)rng.State);
        }
    }

    private static async Task<Dictionary<int, int>> LoadCareerBonusAsync(SaveStore store, Guid saveId, bool lifetime)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<AthleteCareerEntity> careers = await context.AthleteCareers.AsNoTracking().ToListAsync().ConfigureAwait(false);
        return lifetime
            ? careers.ToDictionary(e => e.SaveAthleteId, e => e.LifetimeEarnedBonusThousandths)
            : careers.ToDictionary(e => e.SaveAthleteId, e => e.CurrentEffectiveBonusThousandths);
    }

    private static async Task<(int Stages, int Seasons, int Rounds, long Championship)> CaptureLeagueCountsAsync(
        SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int stages = await context.StageStandings.CountAsync().ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync().ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync().ConfigureAwait(false);
        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths).ConfigureAwait(false);
        return (stages, seasons, rounds, championship);
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

    private static async Task MarkSeasonTwoCompleteAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = new() { SeasonNumber = 2, HasSuperleague = true, IsComplete = true };
        context.Seasons.Add(seasonTwo);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-cup-ind-" + Guid.NewGuid().ToString("N"));
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
