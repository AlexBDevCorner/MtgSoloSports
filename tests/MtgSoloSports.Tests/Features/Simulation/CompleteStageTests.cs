using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Simulation;

public sealed class CompleteStageTests
{
    [Fact]
    public async Task CompleteStage_FromZero_PersistsSixteenRoundsAndStandings()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Stage One", 101UL, 202UL, UniverseTestCatalog.Build());
            int leagueId = await FirstLeagueIdAsync(store, created.Detail.SaveId);

            CompleteStageHandler handler = new(store);
            CompleteStageResponse response = await handler.HandleAsync(created.Detail.SaveId, leagueId);

            AssertStageOneResponse(response);

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            await AssertStageOnePersistenceAsync(context, leagueId);
            await AssertStageScoresMatchFinalsAsync(context, leagueId, response);
            await AssertSameStageBonusPendingAsync(context, leagueId);
            await AssertPlacementCountsRecordedAsync(context, leagueId);
            await AssertRngCommittedAsync(context, response);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertStageOneResponse(CompleteStageResponse response)
    {
        response.SeasonNumber.ShouldBe(1);
        response.StageNumber.ShouldBe(1);
        response.CompletedRounds.ShouldBe(16);
        response.RulesVersion.ShouldBe(RulesV1.RulesVersion);
        response.StageChecksum.Length.ShouldBe(64);
        response.NextStageNumber.ShouldBe(2);
        response.Standings.Count.ShouldBe(32);
        response.Standings.Select(s => s.StageRank).ShouldBe(Enumerable.Range(1, 32).ToList());

        // Championship points reuse the 32-position table with no bonus multiplier.
        int[] expectedTable = [77, 67, 58, 50, 43, 37, 32, 28, 25, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1];
        foreach (CompleteStageStanding standing in response.Standings)
        {
            standing.ChampionshipPointsThousandths.ShouldBe(expectedTable[standing.StageRank - 1] * 1000);
        }

        // Winner earned round bonus for 16 rounds plus the stage win bonus.
        CompleteStageStanding winner = response.Standings.Single(s => s.StageRank == 1);
        winner.EarnedBonusThousandths.ShouldBeGreaterThan(0);
    }

    private static async Task AssertStageOnePersistenceAsync(SaveDbContext context, int leagueId)
    {
        (await context.Rounds.CountAsync(e => e.LeagueId == leagueId).ConfigureAwait(false)).ShouldBe(16);
        (await context.StageStandings.CountAsync(e => e.LeagueId == leagueId).ConfigureAwait(false)).ShouldBe(32);

        StageEntity stage = await context.Stages.SingleAsync(e => e.LeagueId == leagueId && e.StageNumber == 1).ConfigureAwait(false);
        stage.CompletedRounds.ShouldBe(16);
        stage.IsComplete.ShouldBeTrue();

        StageEntity next = await context.Stages.SingleAsync(e => e.LeagueId == leagueId && e.StageNumber == 2).ConfigureAwait(false);
        next.CompletedRounds.ShouldBe(0);
        next.IsComplete.ShouldBeFalse();
    }

    private static async Task AssertStageScoresMatchFinalsAsync(SaveDbContext context, int leagueId, CompleteStageResponse response)
    {
        // Stage scores equal the sum of final round points across all 16 payloads.
        Dictionary<int, int> summedFinals = await SumFinalsAsync(context, leagueId, 1).ConfigureAwait(false);
        foreach (CompleteStageStanding standing in response.Standings)
        {
            standing.StageScoreThousandths.ShouldBe(summedFinals[standing.AthleteId]);
        }
    }

    private static async Task AssertSameStageBonusPendingAsync(SaveDbContext context, int leagueId)
    {
        // Pending bonus earned during the stage never affected the same stage:
        // every Stage 1 round ran with zero stage-start active bonus.
        List<RoundEntity> rounds = await context.Rounds.Where(e => e.LeagueId == leagueId).ToListAsync().ConfigureAwait(false);
        foreach (RoundEntity round in rounds)
        {
            RoundPayloadDocument payload = RoundPayloadDocument.FromJson(round.PayloadJson);
            foreach (RoundPayloadEntry entry in payload.Placements)
            {
                entry.ActiveBonusThousandths.ShouldBe(0);
            }
        }
    }

    private static async Task AssertPlacementCountsRecordedAsync(SaveDbContext context, int leagueId)
    {
        // Placement counts/wins are recorded for season tie-breaking later.
        List<StageStandingEntity> standings = await context.StageStandings.Where(e => e.LeagueId == leagueId).ToListAsync().ConfigureAwait(false);
        foreach (StageStandingEntity row in standings)
        {
            row.StageRank.ShouldBeInRange(1, 32);
            List<int>? counts = JsonSerializer.Deserialize<List<int>>(row.RoundPlaceCountsJson);
            counts.ShouldNotBeNull();
            counts.Count.ShouldBe(32);
            counts.Sum().ShouldBe(16);
            counts[0].ShouldBe(row.RoundWins);
        }
    }

    private static async Task AssertRngCommittedAsync(SaveDbContext context, CompleteStageResponse response)
    {
        // RNG-after commits with the stage result.
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            ((ulong)rng.State).ShouldBe(response.RngAfterState);
            ((ulong)rng.Stream).ShouldBe(response.RngAfterStream);
        }
    }

    [Fact]
    public async Task PendingBonus_DoesNotAffectSameStage_ActivatesNextStage()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Stage Bonus", 303UL, 404UL, UniverseTestCatalog.Build());
            int leagueId = await FirstLeagueIdAsync(store, created.Detail.SaveId);

            CompleteStageHandler completer = new(store);
            CompleteStageResponse completed = await completer.HandleAsync(created.Detail.SaveId, leagueId);
            Dictionary<int, int> earnedByAthlete = completed.Standings.ToDictionary(s => s.AthleteId, s => s.EarnedBonusThousandths);

            // Global sync: Stage 2 cannot begin until Stage 1 is complete everywhere.
            await CompleteStageOneForAllOtherLeaguesAsync(store, created.Detail.SaveId, leagueId, completer);

            // Stage 2's first round must run with exactly Stage 1's earned bonus active.
            AdvanceRoundHandler advancer = new(store);
            AdvanceRoundResponse next = await advancer.HandleAsync(created.Detail.SaveId, leagueId);

            next.StageNumber.ShouldBe(2);
            next.RoundNumber.ShouldBe(1);
            foreach (AdvanceRoundPlacement placement in next.Placements)
            {
                placement.ActiveBonusThousandths.ShouldBe(earnedByAthlete[placement.AthleteId]);
                long expectedFinal = (long)placement.BaseThousandths * (1000 + placement.ActiveBonusThousandths) / 1000;
                placement.FinalThousandths.ShouldBe((int)expectedFinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteStage_EquivalentToSixteenAdvanceRounds()
    {
        var (store, root) = CreateStore();
        try
        {
            IReadOnlyList<MtgSoloSports.Features.Catalog.ImportCatalog.CatalogAthlete> catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord viaRounds = await store.CreateAsync("Stage Via Rounds", 9001UL, 7002UL, catalog);
            SaveStore.CreationRecord viaComplete = await store.CreateAsync("Stage Via Complete", 9001UL, 7002UL, catalog);

            int roundsLeague = await LeagueIdByColorAsync(store, viaRounds.Detail.SaveId, "White");
            int completeLeague = await LeagueIdByColorAsync(store, viaComplete.Detail.SaveId, "White");

            // Path A: sixteen individual AdvanceRound operations, then finalize.
            AdvanceRoundHandler advancer = new(store);
            List<string> roundChecksumsA = new(16);
            for (int i = 1; i <= 16; i++)
            {
                AdvanceRoundResponse round = await advancer.HandleAsync(viaRounds.Detail.SaveId, roundsLeague);
                round.RoundNumber.ShouldBe(i);
                roundChecksumsA.Add(round.PayloadChecksum);
            }

            CompleteStageHandler completer = new(store);
            CompleteStageResponse finalizedA = await completer.HandleAsync(viaRounds.Detail.SaveId, roundsLeague);

            // Path B: one complete-stage command from the same start state.
            CompleteStageResponse completedB = await completer.HandleAsync(viaComplete.Detail.SaveId, completeLeague);

            // Sporting-result equivalence: identical round payloads, standings and RNG.
            List<string> roundChecksumsB = await RoundChecksumsAsync(store, viaComplete.Detail.SaveId, completeLeague);
            roundChecksumsB.ShouldBe(roundChecksumsA);
            finalizedA.StageChecksum.ShouldBe(completedB.StageChecksum);
            finalizedA.RngAfterState.ShouldBe(completedB.RngAfterState);
            finalizedA.RngAfterStream.ShouldBe(completedB.RngAfterStream);

            List<CompleteStageStanding> standingsA = finalizedA.Standings.OrderBy(s => s.StageRank).ToList();
            List<CompleteStageStanding> standingsB = completedB.Standings.OrderBy(s => s.StageRank).ToList();
            standingsA.Count.ShouldBe(standingsB.Count);
            for (int i = 0; i < standingsA.Count; i++)
            {
                standingsA[i].AthleteId.ShouldBe(standingsB[i].AthleteId);
                standingsA[i].StageScoreThousandths.ShouldBe(standingsB[i].StageScoreThousandths);
                standingsA[i].ChampionshipPointsThousandths.ShouldBe(standingsB[i].ChampionshipPointsThousandths);
                standingsA[i].EarnedBonusThousandths.ShouldBe(standingsB[i].EarnedBonusThousandths);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteStage_PartialProgress_CompletesRemainingRounds()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Stage Partial", 505UL, 606UL, UniverseTestCatalog.Build());
            int leagueId = await FirstLeagueIdAsync(store, created.Detail.SaveId);

            AdvanceRoundHandler advancer = new(store);
            List<string> firstFive = new(5);
            for (int i = 1; i <= 5; i++)
            {
                AdvanceRoundResponse round = await advancer.HandleAsync(created.Detail.SaveId, leagueId);
                firstFive.Add(round.PayloadChecksum);
            }

            CompleteStageHandler completer = new(store);
            CompleteStageResponse completed = await completer.HandleAsync(created.Detail.SaveId, leagueId);

            completed.StageNumber.ShouldBe(1);
            completed.CompletedRounds.ShouldBe(16);
            completed.Standings.Count.ShouldBe(32);

            List<string> allChecksums = await RoundChecksumsAsync(store, created.Detail.SaveId, leagueId);
            allChecksums.Count.ShouldBe(16);
            allChecksums.Take(5).ShouldBe(firstFive);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteStage_Stage32_PendingBonusOnlyForNextSeason()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Stage ThirtyTwo", 707UL, 808UL, UniverseTestCatalog.Build());
            int leagueId = await FirstLeagueIdAsync(store, created.Detail.SaveId);

            CompleteStageHandler completer = new(store);
            CompleteStageResponse last = await CompleteAllStagesAsync(store, created.Detail.SaveId, leagueId, completer);

            // Stage 32 completes the season: no next stage cursor is created.
            last.NextStageNumber.ShouldBeNull();

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            await AssertSeasonCompleteAsync(context, leagueId);
            await AssertStage32PendingAsync(context, leagueId);
            await AssertStage32DecaysToNextSeasonAsync(context, leagueId);

            // Completing Stage 32 again conflicts; the season is finished.
            await Should.ThrowAsync<CompleteStageConflictException>(() => completer.HandleAsync(created.Detail.SaveId, leagueId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<CompleteStageResponse> CompleteAllStagesAsync(
        SaveStore store, Guid saveId, int leagueId, CompleteStageHandler completer)
    {
        // Global sync: stages progress synchronously across all active leagues.
        List<int> allLeagues = await AllLeagueIdsAsync(store, saveId).ConfigureAwait(false);
        CompleteStageResponse last = null!;
        for (int stage = 1; stage <= 32; stage++)
        {
            foreach (int otherLeagueId in allLeagues)
            {
                CompleteStageResponse completed = await completer.HandleAsync(saveId, otherLeagueId).ConfigureAwait(false);
                completed.StageNumber.ShouldBe(stage);
                completed.CompletedRounds.ShouldBe(16);
                if (otherLeagueId == leagueId)
                {
                    last = completed;
                }
            }
        }

        return last;
    }

    private static async Task CompleteStageOneForAllOtherLeaguesAsync(
        SaveStore store, Guid saveId, int exceptLeagueId, CompleteStageHandler completer)
    {
        List<int> allLeagues = await AllLeagueIdsAsync(store, saveId).ConfigureAwait(false);
        foreach (int otherLeagueId in allLeagues)
        {
            if (otherLeagueId == exceptLeagueId)
            {
                continue;
            }

            await completer.HandleAsync(saveId, otherLeagueId).ConfigureAwait(false);
        }
    }

    private static async Task<List<int>> AllLeagueIdsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Leagues.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync().ConfigureAwait(false);
    }

    private static async Task AssertSeasonCompleteAsync(SaveDbContext context, int leagueId)
    {
        (await context.Rounds.CountAsync(e => e.LeagueId == leagueId).ConfigureAwait(false)).ShouldBe(32 * 16);
        (await context.StageStandings.CountAsync(e => e.LeagueId == leagueId).ConfigureAwait(false)).ShouldBe(32 * 32);
        (await context.Stages.CountAsync(e => e.LeagueId == leagueId).ConfigureAwait(false)).ShouldBe(32);
    }

    private static async Task AssertStage32PendingAsync(SaveDbContext context, int leagueId)
    {
        // Stage 32's own rounds ran without Stage 32's pending bonus active.
        Dictionary<int, int> earnedThrough31 = await SumEarnedAsync(context, leagueId, 1, 31).ConfigureAwait(false);
        List<RoundEntity> stage32Rounds = await context.Rounds
            .Where(e => e.LeagueId == leagueId && e.StageNumber == 32)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync().ConfigureAwait(false);
        stage32Rounds.Count.ShouldBe(16);
        RoundPayloadDocument firstRound = RoundPayloadDocument.FromJson(stage32Rounds[0].PayloadJson);
        foreach (RoundPayloadEntry entry in firstRound.Placements)
        {
            entry.ActiveBonusThousandths.ShouldBe(earnedThrough31[entry.AthleteId]);
        }
    }

    private static async Task AssertStage32DecaysToNextSeasonAsync(SaveDbContext context, int leagueId)
    {
        // Stage 32 earned bonus is persisted as pending and enters the next
        // season at 80% weight per the linear decay table.
        Dictionary<int, int> earned32 = await SumEarnedAsync(context, leagueId, 32, 32).ConfigureAwait(false);
        earned32.Values.Any(v => v > 0).ShouldBeTrue();

        RulesV1 rules = RulesV1.CreateDefault();
        List<StageStandingEntity> allStandings = await context.StageStandings.Where(e => e.LeagueId == leagueId).ToListAsync().ConfigureAwait(false);
        Dictionary<int, SeasonEntity> seasons = await context.Seasons.ToDictionaryAsync(e => e.Id).ConfigureAwait(false);
        foreach (int athleteId in earned32.Keys.Take(5))
        {
            List<BonusContribution> contributions = BuildContributions(allStandings, seasons, athleteId);
            int totalSeason1 = contributions.Where(c => c.EarnedSeason == 1).Sum(c => c.Earned.Thousandths);
            int expectedNextSeason = totalSeason1 * 800 / 1000;
            BonusCalculator.EffectiveBonus(contributions, currentSeason: 2, currentStage: 1, rules)
                .Thousandths.ShouldBe(expectedNextSeason);
        }
    }

    private static List<BonusContribution> BuildContributions(
        List<StageStandingEntity> allStandings, Dictionary<int, SeasonEntity> seasons, int athleteId)
    {
        List<BonusContribution> contributions = [];
        foreach (StageStandingEntity row in allStandings.Where(r => r.SaveAthleteId == athleteId))
        {
            contributions.Add(new BonusContribution(
                seasons[row.SeasonId].SeasonNumber,
                row.StageNumber,
                MtgSoloSports.SimulationKernel.FixedPoint.Bonus.FromThousandths(row.EarnedBonusThousandths)));
        }

        return contributions;
    }

    [Fact]
    public async Task CompleteStage_UnknownLeague_ThrowsConflict_UnknownSave_ThrowsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Stage Errors", 909UL, 910UL, UniverseTestCatalog.Build());
            CompleteStageHandler handler = new(store);

            await Should.ThrowAsync<AdvanceRoundConflictException>(() => handler.HandleAsync(created.Detail.SaveId, 999999));
            await Should.ThrowAsync<SaveNotFoundException>(() => handler.HandleAsync(Guid.NewGuid(), 1));
            await Should.ThrowAsync<ArgumentException>(() => handler.HandleAsync(Guid.Empty, 1));
            await Should.ThrowAsync<ArgumentException>(() => handler.HandleAsync(created.Detail.SaveId, 0));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Dictionary<int, int>> SumFinalsAsync(SaveDbContext context, int leagueId, int stageNumber)
    {
        List<RoundEntity> rounds = await context.Rounds
            .AsNoTracking()
            .Where(e => e.LeagueId == leagueId && e.StageNumber == stageNumber)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync()
            .ConfigureAwait(false);
        Dictionary<int, int> sums = new(32);
        foreach (RoundEntity round in rounds)
        {
            RoundPayloadDocument payload = RoundPayloadDocument.FromJson(round.PayloadJson);
            foreach (RoundPayloadEntry entry in payload.Placements)
            {
                sums.TryGetValue(entry.AthleteId, out int current);
                sums[entry.AthleteId] = checked(current + entry.FinalThousandths);
            }
        }

        return sums;
    }

    private static async Task<Dictionary<int, int>> SumEarnedAsync(SaveDbContext context, int leagueId, int fromStage, int toStage)
    {
        List<StageStandingEntity> rows = await context.StageStandings
            .AsNoTracking()
            .Where(e => e.LeagueId == leagueId && e.StageNumber >= fromStage && e.StageNumber <= toStage)
            .ToListAsync()
            .ConfigureAwait(false);
        Dictionary<int, int> sums = new(32);
        foreach (StageStandingEntity row in rows)
        {
            sums.TryGetValue(row.SaveAthleteId, out int current);
            sums[row.SaveAthleteId] = checked(current + row.EarnedBonusThousandths);
        }

        return sums;
    }

    private static async Task<List<string>> RoundChecksumsAsync(SaveStore store, Guid saveId, int leagueId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.Rounds
            .AsNoTracking()
            .Where(e => e.LeagueId == leagueId && e.StageNumber == 1)
            .OrderBy(e => e.RoundNumber)
            .Select(e => e.PayloadChecksum)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    private static async Task<int> FirstLeagueIdAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        LeagueEntity league = await context.Leagues.AsNoTracking().OrderBy(e => e.Id).FirstAsync().ConfigureAwait(false);
        return league.Id;
    }

    private static async Task<int> LeagueIdByColorAsync(SaveStore store, Guid saveId, string color)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<LeagueEntity> leagues = await context.Leagues.AsNoTracking().ToListAsync().ConfigureAwait(false);
        LeagueEntity league = leagues.Single(l => string.Equals(l.Name, $"{color} League", StringComparison.Ordinal));
        return league.Id;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-stage-" + Guid.NewGuid().ToString("N"));
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
