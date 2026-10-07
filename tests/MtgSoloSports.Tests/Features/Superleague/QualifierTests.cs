using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.GetQualifierResult;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Superleague;

public sealed class QualifierTests
{
    [Fact]
    public async Task Run_BeforeAutomaticMovement_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Qual Early", 111UL, 222UL, UniverseTestCatalog.Build());
            RunQualifierHandler handler = new(store);
            await Should.ThrowAsync<RunQualifierConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
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
            SaveStore.CreationRecord created = await store.CreateAsync("Qual Missing", 333UL, 444UL, UniverseTestCatalog.Build());
            GetQualifierResultHandler query = new(store);
            await Should.ThrowAsync<QualifierResultNotFoundException>(
                () => query.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Run_AfterAutomaticMovement_Produces32And8AndPreservesHistory()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Qual Full", 707UL, 808UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);
            await FillSeasonTwoFeedersAsync(store, created.Detail.SaveId);
            await InsertSyntheticSeasonTwoStandingsAsync(store, created.Detail.SaveId);

            ResolveAutomaticMovementHandler movement = new(store);
            ResolveAutomaticMovementResponse plan = await movement.HandleAsync(created.Detail.SaveId);

            (int stages, int seasons, int rounds, int qualifierRounds, int qualifierStandings, long lifetime, ulong rng, long championship) =
                await CapturePreservationAsync(store, created.Detail.SaveId);
            qualifierRounds.ShouldBe(0);
            qualifierStandings.ShouldBe(0);

            RunQualifierHandler handler = new(store);
            RunQualifierResponse response = await handler.HandleAsync(created.Detail.SaveId);

            AssertField(response, plan);
            AssertQualified(response);
            await AssertPersistedAsync(store, created.Detail.SaveId, response);
            await AssertPreservationAsync(store, created.Detail.SaveId, stages, seasons, rounds, lifetime, championship, rng);
            await AssertQueryMatchesAsync(store, created.Detail.SaveId, response);

            await Should.ThrowAsync<RunQualifierConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
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
        var (store, root, saveId) = await PrepareQualifierForSeedAsync(4242UL, 777UL);
        var (secondStore, secondRoot, secondId) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-qual-det-");
        try
        {
            RunQualifierResponse first = await new RunQualifierHandler(store).HandleAsync(saveId);
            RunQualifierResponse second = await new RunQualifierHandler(secondStore).HandleAsync(secondId);
            first.Checksum.ShouldBe(second.Checksum);
            first.Standings.Select(m => m.AthleteId).ShouldBe(second.Standings.Select(m => m.AthleteId).ToList());
            first.Standings.Select(m => m.QualifierScoreThousandths).ShouldBe(second.Standings.Select(m => m.QualifierScoreThousandths).ToList());
            first.RngBeforeState.ShouldBe(second.RngBeforeState);
            first.RngAfterState.ShouldBe(second.RngAfterState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            TestSaveStores.DeleteRoot(secondRoot);
        }
    }

    [Fact]
    public async Task Run_ActiveBonusAppliesIdenticallyToBothRoles()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Qual Bonus", 9001UL, 7002UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);
            await FillSeasonTwoFeedersAsync(store, created.Detail.SaveId);
            await InsertSyntheticSeasonTwoStandingsAsync(store, created.Detail.SaveId);

            ResolveAutomaticMovementHandler movement = new(store);
            await movement.HandleAsync(created.Detail.SaveId);

            (int incumbentId, int challengerId) = await InsertEqualBonusAsync(store, created.Detail.SaveId);

            RunQualifierHandler handler = new(store);
            RunQualifierResponse response = await handler.HandleAsync(created.Detail.SaveId);

            int incumbentBonus = await LoadRoundOneActiveBonusAsync(store, created.Detail.SaveId, incumbentId);
            int challengerBonus = await LoadRoundOneActiveBonusAsync(store, created.Detail.SaveId, challengerId);
            int expectedIncumbent = await ComputeExpectedActiveBonusAsync(store, created.Detail.SaveId, incumbentId);
            int expectedChallenger = await ComputeExpectedActiveBonusAsync(store, created.Detail.SaveId, challengerId);
            incumbentBonus.ShouldBe(expectedIncumbent);
            challengerBonus.ShouldBe(expectedChallenger);
            incumbentBonus.ShouldBeGreaterThan(0);
            challengerBonus.ShouldBeGreaterThan(0);

            QualifierStandingMember incumbent = response.Standings.Single(m => m.AthleteId == incumbentId);
            QualifierStandingMember challenger = response.Standings.Single(m => m.AthleteId == challengerId);
            incumbent.Role.ShouldBe(QualifierRole.Incumbent.ToString());
            challenger.Role.ShouldBe(QualifierRole.Challenger.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertField(RunQualifierResponse response, ResolveAutomaticMovementResponse plan)
    {
        response.QualifierSize.ShouldBe(32);
        response.Rounds.ShouldBe(16);
        response.Winners.ShouldBe(8);
        response.Standings.Count.ShouldBe(32);

        HashSet<int> incumbentIds = plan.QualifierIncumbents.Select(m => m.AthleteId).ToHashSet();
        HashSet<int> challengerIds = plan.QualifierChallengers.Select(m => m.AthleteId).ToHashSet();
        HashSet<int> fieldIds = response.Standings.Select(m => m.AthleteId).ToHashSet();
        fieldIds.SetEquals(incumbentIds.Union(challengerIds)).ShouldBeTrue();

        response.Standings.Count(m => string.Equals(m.Role, QualifierRole.Incumbent.ToString(), StringComparison.Ordinal)).ShouldBe(8);
        response.Standings.Count(m => string.Equals(m.Role, QualifierRole.Challenger.ToString(), StringComparison.Ordinal)).ShouldBe(24);

        foreach (QualifierStandingMember member in response.Standings.Where(m => string.Equals(m.Role, QualifierRole.Incumbent.ToString(), StringComparison.Ordinal)))
        {
            member.FromSeasonRank.ShouldBeInRange(17, 24);
        }

        foreach (QualifierStandingMember member in response.Standings.Where(m => string.Equals(m.Role, QualifierRole.Challenger.ToString(), StringComparison.Ordinal)))
        {
            member.FromSeasonRank.ShouldBeInRange(2, 4);
        }

        foreach (var group in response.Standings.Where(m => string.Equals(m.Role, QualifierRole.Challenger.ToString(), StringComparison.Ordinal)).GroupBy(m => m.FromLeagueId))
        {
            group.Count().ShouldBe(3);
        }

        response.Standings.Select(m => m.QualifierRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, 32).ToList());
    }

    private static void AssertQualified(RunQualifierResponse response)
    {
        response.Standings.Count(m => m.IsQualified).ShouldBe(8);
        response.Standings.Where(m => m.IsQualified).Select(m => m.QualifierRank).OrderBy(r => r)
            .ShouldBe(Enumerable.Range(1, 8).ToList());
        response.Standings.Where(m => !m.IsQualified).Select(m => m.QualifierRank).OrderBy(r => r)
            .ShouldBe(Enumerable.Range(9, 24).ToList());
        (response.IncumbentQualifiedCount + response.ChallengerQualifiedCount).ShouldBe(8);
    }

    private static async Task AssertPersistedAsync(SaveStore store, Guid saveId, RunQualifierResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.FromSeasonNumber).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.ToSeasonNumber).ConfigureAwait(false);

        List<QualifierRoundEntity> rounds = await context.QualifierRounds.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .OrderBy(e => e.RoundNumber).ToListAsync().ConfigureAwait(false);
        rounds.Count.ShouldBe(16);
        rounds.Select(r => r.RoundNumber).ShouldBe(Enumerable.Range(1, 16).ToList());
        foreach (QualifierRoundEntity round in rounds)
        {
            QualifierRoundPayloadDocument document = QualifierRoundPayloadDocument.FromJson(round.PayloadJson);
            document.Placements.Count.ShouldBe(32);
            document.Checksum.ShouldBe(round.PayloadChecksum);
        }

        List<QualifierStandingEntity> standings = await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .OrderBy(e => e.QualifierRank).ToListAsync().ConfigureAwait(false);
        standings.Count.ShouldBe(32);
        standings.Count(s => s.IsQualified).ShouldBe(8);
        standings.Select(s => s.QualifierRank).ShouldBe(Enumerable.Range(1, 32).ToList());
    }

    private static async Task AssertPreservationAsync(
        SaveStore store,
        Guid saveId,
        int stages,
        int seasons,
        int rounds,
        long lifetime,
        long championship,
        ulong rngBefore)
    {
        (int stagesAfter, int seasonsAfter, int roundsAfter, int _, int _, long lifetimeAfter, ulong rngAfter, long championshipAfter) =
            await CapturePreservationAsync(store, saveId).ConfigureAwait(false);
        stagesAfter.ShouldBe(stages);
        seasonsAfter.ShouldBe(seasons);
        roundsAfter.ShouldBe(rounds);
        lifetimeAfter.ShouldBe(lifetime);
        championshipAfter.ShouldBe(championship);
        rngAfter.ShouldNotBe(rngBefore);
    }

    private static async Task AssertQueryMatchesAsync(SaveStore store, Guid saveId, RunQualifierResponse response)
    {
        GetQualifierResultHandler query = new(store);
        GetQualifierResultResponse summary = await query.HandleAsync(saveId).ConfigureAwait(false);
        summary.FromSeasonNumber.ShouldBe(response.FromSeasonNumber);
        summary.ToSeasonNumber.ShouldBe(response.ToSeasonNumber);
        summary.Winners.ShouldBe(8);
        summary.Standings.Count.ShouldBe(32);
        summary.Checksum.ShouldNotBeNullOrWhiteSpace();
        summary.Standings.Select(m => m.AthleteId).ShouldBe(response.Standings.Select(m => m.AthleteId).ToList());
        summary.Standings.Select(m => m.QualifierRank).ShouldBe(response.Standings.Select(m => m.QualifierRank).ToList());

        GetQualifierResultResponse again = await query.HandleAsync(saveId, fromSeasonNumber: response.FromSeasonNumber).ConfigureAwait(false);
        again.Winners.ShouldBe(8);
        again.RoundCount.ShouldBe(16);
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareQualifierForSeedAsync(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Qual Det", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        await CompleteSeasonOneAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        CreateInauguralSuperleagueHandler inaugural = new(store);
        await inaugural.HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        await FillSeasonTwoFeedersAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        await InsertSyntheticSeasonTwoStandingsAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        ResolveAutomaticMovementHandler movement = new(store);
        await movement.HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        return (store, root, created.Detail.SaveId);
    }

    private static async Task<(int StageCount, int SeasonCount, int RoundCount, int QualifierRounds, int QualifierStandings, long Lifetime, ulong Rng, long Championship)> CapturePreservationAsync(
        SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int stages = await context.StageStandings.CountAsync().ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync().ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync().ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync().ConfigureAwait(false);
        int qualifierStandings = await context.QualifierStandings.CountAsync().ConfigureAwait(false);
        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths).ConfigureAwait(false);
        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths).ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return (stages, seasons, rounds, qualifierRounds, qualifierStandings, lifetime, (ulong)rng.State, championship);
        }
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

    private static async Task FillSeasonTwoFeedersAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<LeagueEntity> feeders = await context.Leagues
            .Where(e => e.SeasonId == seasonTwo.Id && e.Kind == (int)LeagueKind.Feeder && e.FeederDivision == (int)MtgSoloSports.SimulationKernel.Leagues.FeederDivision.First)
            .ToListAsync().ConfigureAwait(false);
        foreach (LeagueEntity feeder in feeders.OrderBy(l => l.SportingColor))
        {
            List<SeasonMembershipEntity> poolForColor = await context.SeasonMemberships
                .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == null && e.SportingColor == feeder.SportingColor)
                .OrderBy(e => e.SaveAthleteId)
                .Take(4)
                .ToListAsync().ConfigureAwait(false);
            foreach (SeasonMembershipEntity pool in poolForColor)
            {
                pool.LeagueId = feeder.Id;
            }
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task InsertSyntheticSeasonTwoStandingsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == seasonTwo.Id)
            .OrderBy(e => e.Id)
            .ToListAsync().ConfigureAwait(false);
        foreach (LeagueEntity league in leagues)
        {
            List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
                .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == league.Id)
                .ToListAsync().ConfigureAwait(false);
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

    private static async Task<(int IncumbentId, int ChallengerId)> InsertEqualBonusAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);

        MovementEntity incumbentMove = await context.Movements
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.QualifierIncumbent)
            .OrderBy(e => e.SaveAthleteId)
            .FirstAsync().ConfigureAwait(false);
        MovementEntity challengerMove = await context.Movements
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.Kind == (int)MovementKind.QualifierChallenger)
            .OrderBy(e => e.SaveAthleteId)
            .FirstAsync().ConfigureAwait(false);

        await InsertBonusRowAsync(context, source, incumbentMove).ConfigureAwait(false);
        await InsertBonusRowAsync(context, source, challengerMove).ConfigureAwait(false);

        await context.SaveChangesAsync().ConfigureAwait(false);
        return (incumbentMove.SaveAthleteId, challengerMove.SaveAthleteId);
    }

    private static Task InsertBonusRowAsync(SaveDbContext context, SeasonEntity source, MovementEntity move)
    {
        context.StageStandings.Add(new StageStandingEntity
        {
            SeasonId = source.Id,
            LeagueId = move.FromLeagueId,
            StageId = 100000 + move.SaveAthleteId,
            StageNumber = 1,
            SaveAthleteId = move.SaveAthleteId,
            StageRank = 1,
            StageScoreThousandths = 100000,
            BaseScoreThousandths = 90000,
            ChampionshipPointsThousandths = 77000,
            RoundWins = 2,
            RoundPlaceCountsJson = "[2,2,2,2,2,2,2,2,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]",
            EarnedBonusThousandths = 100,
        });
        return Task.CompletedTask;
    }

    private static async Task<int> LoadRoundOneActiveBonusAsync(SaveStore store, Guid saveId, int athleteId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);
        QualifierRoundEntity round = await context.QualifierRounds.AsNoTracking()
            .SingleAsync(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id && e.RoundNumber == 1).ConfigureAwait(false);
        QualifierRoundPayloadDocument document = QualifierRoundPayloadDocument.FromJson(round.PayloadJson);
        return document.Placements.Single(p => p.AthleteId == athleteId).ActiveBonusThousandths;
    }

    private static async Task<int> ComputeExpectedActiveBonusAsync(SaveStore store, Guid saveId, int athleteId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        Dictionary<int, int> seasonNumbers = await context.Seasons.AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.SeasonNumber).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);
        int nextNumber = seasonNumbers[next.Id];
        List<StageStandingEntity> rows = await context.StageStandings.AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId).ToListAsync().ConfigureAwait(false);
        List<MtgSoloSports.SimulationKernel.Scoring.BonusContribution> contributions = new(rows.Count);
        foreach (StageStandingEntity row in rows)
        {
            int earnedSeason = seasonNumbers[row.SeasonId];
            contributions.Add(new MtgSoloSports.SimulationKernel.Scoring.BonusContribution(
                earnedSeason,
                row.StageNumber,
                MtgSoloSports.SimulationKernel.FixedPoint.Bonus.FromThousandths(row.EarnedBonusThousandths)));
        }

        MtgSoloSports.SimulationKernel.FixedPoint.Bonus effective =
            MtgSoloSports.SimulationKernel.Scoring.BonusCalculator.EffectiveBonus(contributions, nextNumber, 1, MtgSoloSports.SimulationKernel.Rules.RulesV1.CreateDefault());
        return effective.Thousandths;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-qual-" + Guid.NewGuid().ToString("N"));
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
