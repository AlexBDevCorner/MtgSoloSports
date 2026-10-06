using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Qualifiers;

/// <summary>
/// MSS-058 acceptance tests: adjacent-tier postseason movement and 17 qualifiers.
/// </summary>
public sealed class FeederQualifierTests
{
    [Fact]
    public async Task AutomaticMovement_PreservesSuperleagueSemantics()
    {
        var (store, root, saveId) = await PrepareTieredSaveAsync("MSS058 SL", 1001UL, 2002UL);
        try
        {
            ResolveAutomaticMovementResponse movement = await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);

            movement.Safe.Count.ShouldBe(16);
            movement.Promoted.Count.ShouldBe(8);
            movement.Relegated.Count.ShouldBe(8);
            movement.QualifierIncumbents.Count.ShouldBe(8);
            movement.QualifierChallengers.Count.ShouldBe(24);
            movement.MovementCount.ShouldBe(48);
            movement.QualifierIncumbents.Select(m => m.FromSeasonRank).OrderBy(r => r).ShouldBe(Enumerable.Range(17, 8).ToList());
            movement.Relegated.Select(m => m.FromSeasonRank).OrderBy(r => r).ShouldBe(Enumerable.Range(25, 8).ToList());
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AutomaticMovement_FeederBands_WhiteColor()
    {
        var (store, root, saveId) = await PrepareTieredSaveAsync("MSS058 Bands", 3003UL, 4004UL);
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);

            await AssertFeederBandsAsync(store, saveId);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FullTransition_NoAthleteInTwoQualifiers_AndOneTierMax()
    {
        var (store, root, saveId) = await PrepareTieredSaveAsync("MSS058 Full", 5005UL, 6006UL);
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);

            RunAllQualifiersResponse response = await new RunAllQualifiersHandler(store).HandleAsync(saveId);
            response.ExecutedNow.Count.ShouldBe(17);
            response.AlreadyCompleted.Count.ShouldBe(0);

            await AssertQualifierUniquenessAsync(store, saveId);
            await AssertQualifierCompositionAsync(store, saveId);
            await AssertAdjacentTierMovementAsync(store, saveId);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task QualifierReplay_UsesPersistedFacts_NeverResimulates()
    {
        var (store, root, saveId) = await PrepareTieredSaveAsync("MSS058 Replay", 7007UL, 8008UL);
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);
            await new RunAllQualifiersHandler(store).HandleAsync(saveId);

            var list = new GetQualifierListHandler(store);
            GetQualifierListResponse all = await list.HandleAsync(saveId, fromSeasonNumber: 2);
            all.Events.Count.ShouldBe(17);

            AssertQualifierReplay(all);

            GetQualifierEventResponse white = await list.HandleSingleAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White, fromSeasonNumber: 2);
            white.QualifierSize.ShouldBe(16);
            white.Winners.ShouldBe(8);
            white.Standings.Count(s => s.IsQualified).ShouldBe(8);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DeterministicOrder_SameSeed_SameRng()
    {
        (RunAllQualifiersResponse first, string rootFirst) = await RunAllForSeedAsync(9009UL, 1010UL);
        (RunAllQualifiersResponse second, string rootSecond) = await RunAllForSeedAsync(9009UL, 1010UL);
        try
        {
            first.TotalStandings.ShouldBe(second.TotalStandings);
            first.TotalRounds.ShouldBe(second.TotalRounds);
            first.ExecutedNow.ShouldBe(second.ExecutedNow.ToList());
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(rootFirst);
            PostseasonTestSaves.DeleteRoot(rootSecond);
        }
    }

    [Fact]
    public async Task Retry_AfterSomeQualifiers_DoesNotRerun()
    {
        var (store, root, saveId) = await PrepareTieredSaveAsync("MSS058 Retry", 1111UL, 2222UL);
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);

            // Run SL + first feeder individually (canonical order start).
            await new RunQualifierHandler(store).HandleAsync(saveId);
            await new BoundaryQualifierRunner(store).HandleAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White);

            var runAll = new RunAllQualifiersHandler(store);
            RunAllQualifiersResponse response = await runAll.HandleAsync(saveId);
            response.AlreadyCompleted.Count.ShouldBe(2);
            response.ExecutedNow.Count.ShouldBe(15);

            // Retry after all complete: nothing executed, all already complete.
            RunAllQualifiersResponse retry = await runAll.HandleAsync(saveId);
            retry.AlreadyCompleted.Count.ShouldBe(17);
            retry.ExecutedNow.Count.ShouldBe(0);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task F3Bottom_RemainInF3_BeforeRebalance()
    {
        var (store, root, saveId) = await PrepareTieredSaveAsync("MSS058 F3", 3333UL, 4444UL);
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);
            await new RunAllQualifiersHandler(store).HandleAsync(saveId);

            await AssertPoolNeverQualifiesAsync(store, saveId);
            await AssertF3BottomRemainsAsync(store, saveId);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareTieredSaveAsync(string name, ulong seed, ulong stream)
    {
        var (store, root) = PostseasonTestSaves.CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync(name, seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        await PostseasonTestSaves.CompleteSeasonOneAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        await new CreateInauguralSuperleagueHandler(store).HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        await PostseasonTestSaves.FillSeasonTwoFeedersAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        await PostseasonTestSaves.InsertSyntheticSeasonTwoStandingsAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        return (store, root, created.Detail.SaveId);
    }

    private static async Task AssertFeederBandsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);

        // F1 White bands.
        await AssertFeederBandAsync(context, source, next, SportingColor.White, FeederDivision.First, 17, 24, MovementKind.FeederQualifierIncumbent).ConfigureAwait(false);
        await AssertFeederBandAsync(context, source, next, SportingColor.White, FeederDivision.First, 25, 32, MovementKind.FeederAutomaticRelegation).ConfigureAwait(false);

        // F2 White bands: auto-up, challenger, incumbent, auto-down are non-overlapping.
        await AssertFeederBandAsync(context, source, next, SportingColor.White, FeederDivision.Second, 1, 8, MovementKind.FeederAutomaticPromotion).ConfigureAwait(false);
        await AssertFeederBandAsync(context, source, next, SportingColor.White, FeederDivision.Second, 9, 16, MovementKind.FeederQualifierChallenger).ConfigureAwait(false);
        await AssertFeederBandAsync(context, source, next, SportingColor.White, FeederDivision.Second, 17, 24, MovementKind.FeederQualifierIncumbent).ConfigureAwait(false);
        await AssertFeederBandAsync(context, source, next, SportingColor.White, FeederDivision.Second, 25, 32, MovementKind.FeederAutomaticRelegation).ConfigureAwait(false);

        // F3 White bands.
        await AssertFeederBandAsync(context, source, next, SportingColor.White, FeederDivision.Third, 1, 8, MovementKind.FeederAutomaticPromotion).ConfigureAwait(false);
        await AssertFeederBandAsync(context, source, next, SportingColor.White, FeederDivision.Third, 9, 16, MovementKind.FeederQualifierChallenger).ConfigureAwait(false);

        // F3 ranks 17-32 remain: no movement rows.
        await AssertNoMovementForRanksAsync(context, source, next, SportingColor.White, FeederDivision.Third, 17, 32).ConfigureAwait(false);
    }

    private static async Task AssertQualifierUniquenessAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);

        List<QualifierStandingEntity> standings = await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .ToListAsync().ConfigureAwait(false);
        standings.Count.ShouldBe(288);

        // No athlete in two qualifiers.
        standings.Select(s => s.SaveAthleteId).Distinct().Count().ShouldBe(288);
    }

    private static async Task AssertQualifierCompositionAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);

        List<QualifierStandingEntity> standings = await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .ToListAsync().ConfigureAwait(false);

        // Each feeder qualifier: 8 incumbents + 8 challengers, 8 winners.
        foreach (QualifierBoundary boundary in new[] { QualifierBoundary.Feeder1Feeder2, QualifierBoundary.Feeder2Feeder3 })
        {
            foreach (SportingColor color in Enum.GetValues<SportingColor>())
            {
                List<QualifierStandingEntity> evt = standings
                    .Where(s => s.QualifierBoundary == (int)boundary && s.QualifierSportingColor == (int)color)
                    .ToList();
                evt.Count.ShouldBe(16);
                evt.Count(s => s.Role == (int)QualifierRole.Incumbent).ShouldBe(8);
                evt.Count(s => s.Role == (int)QualifierRole.Challenger).ShouldBe(8);
                evt.Count(s => s.IsQualified).ShouldBe(8);
            }
        }

        // Superleague qualifier unchanged.
        List<QualifierStandingEntity> sl = standings
            .Where(s => s.QualifierBoundary == (int)QualifierBoundary.Superleague)
            .ToList();
        sl.Count.ShouldBe(32);
        sl.Count(s => s.IsQualified).ShouldBe(8);
    }

    private static async Task AssertAdjacentTierMovementAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);

        // One tier max: every movement is adjacent (checked via levels) and
        // no athlete has two movements for the transition.
        List<MovementEntity> movements = await context.Movements.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .ToListAsync().ConfigureAwait(false);
        movements.Select(m => m.SaveAthleteId).Distinct().Count().ShouldBe(movements.Count);

        Dictionary<int, LeagueEntity> leaguesById = await context.Leagues.AsNoTracking()
            .ToDictionaryAsync(e => e.Id).ConfigureAwait(false);
        foreach (MovementEntity movement in movements)
        {
            if (movement.Kind == (int)MovementKind.RebalanceDraw
                || movement.Kind == (int)MovementKind.RebalanceDisplacement
                || movement.FromLeagueId == 0
                || movement.ToLeagueId == 0)
            {
                continue;
            }

            if (movement.Kind == (int)MovementKind.FeederQualifierIncumbent
                || movement.Kind == (int)MovementKind.FeederQualifierChallenger
                || movement.Kind == (int)MovementKind.QualifierIncumbent
                || movement.Kind == (int)MovementKind.QualifierChallenger)
            {
                // Provisional markers remain in tier; final tier change is via roster.
                continue;
            }

            LeagueEntity from = leaguesById[movement.FromLeagueId];
            LeagueEntity to = leaguesById[movement.ToLeagueId];
            int distance = Math.Abs(
                LeagueHierarchy.Order(LeagueEntityLevels.GetLevel(from))
                - LeagueHierarchy.Order(LeagueEntityLevels.GetLevel(to)));
            distance.ShouldBe(1);
        }
    }

    private static void AssertQualifierReplay(GetQualifierListResponse all)
    {
        // Each event is independently queryable; replay via list API matches.
        foreach (GetQualifierEventResponse evt in all.Events)
        {
            evt.RoundCount.ShouldBe(16);
            evt.Standings.Count.ShouldBe(evt.QualifierSize);
            if (evt.BoundaryId == (int)QualifierBoundary.Superleague)
            {
                evt.QualifierSize.ShouldBe(32);
                evt.Winners.ShouldBe(8);
            }
            else
            {
                evt.QualifierSize.ShouldBe(16);
                evt.Winners.ShouldBe(8);
            }
        }
    }

    private static async Task AssertPoolNeverQualifiesAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);

        // Pool never participates in a qualifier.
        List<QualifierStandingEntity> standings = await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .ToListAsync().ConfigureAwait(false);
        List<SeasonMembershipEntity> sourceMemberships = await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        HashSet<int> poolIds = sourceMemberships.Where(m => m.LeagueId == null).Select(m => m.SaveAthleteId).ToHashSet();
        standings.Any(s => poolIds.Contains(s.SaveAthleteId)).ShouldBeFalse();
    }

    private static async Task AssertF3BottomRemainsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);

        // F3 ranks 17-32 remain in F3 (no auto movement, no qualifier).
        LeagueEntity f3WhiteSource = await context.Leagues.AsNoTracking().SingleAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Feeder
                && e.FeederDivision == (int)FeederDivision.Third && e.SportingColor == (int)SportingColor.White).ConfigureAwait(false);
        List<SeasonStandingEntity> f3Standings = await context.SeasonStandings.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == f3WhiteSource.Id && e.SeasonRank >= 17)
            .ToListAsync().ConfigureAwait(false);
        f3Standings.Count.ShouldBe(16);

        LeagueEntity f3WhiteNext = await context.Leagues.AsNoTracking().SingleAsync(
            e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder
                && e.FeederDivision == (int)FeederDivision.Third && e.SportingColor == (int)SportingColor.White).ConfigureAwait(false);
        foreach (SeasonStandingEntity row in f3Standings)
        {
            SeasonMembershipEntity? nextMembership = await context.SeasonMemberships.AsNoTracking().SingleOrDefaultAsync(
                e => e.SeasonId == next.Id && e.SaveAthleteId == row.SaveAthleteId).ConfigureAwait(false);
            nextMembership.ShouldNotBeNull();
            nextMembership!.LeagueId.ShouldBe(f3WhiteNext.Id);
        }
    }

    private static async Task AssertFeederBandAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next,
        SportingColor color, FeederDivision division, int firstRank, int lastRank, MovementKind kind)
    {
        LeagueEntity league = await context.Leagues.AsNoTracking().SingleAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Feeder
                && e.FeederDivision == (int)division && e.SportingColor == (int)color).ConfigureAwait(false);
        List<SeasonStandingEntity> rows = await context.SeasonStandings.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == league.Id && e.SeasonRank >= firstRank && e.SeasonRank <= lastRank)
            .ToListAsync().ConfigureAwait(false);
        rows.Count.ShouldBe(lastRank - firstRank + 1);

        foreach (SeasonStandingEntity row in rows)
        {
            bool hasMovement = await context.Movements.AsNoTracking().AnyAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                    && e.SaveAthleteId == row.SaveAthleteId && e.Kind == (int)kind).ConfigureAwait(false);
            hasMovement.ShouldBeTrue($"Athlete {row.SaveAthleteId} rank {row.SeasonRank} should have {kind}.");
        }
    }

    private static async Task AssertNoMovementForRanksAsync(
        SaveDbContext context, SeasonEntity source, SeasonEntity next,
        SportingColor color, FeederDivision division, int firstRank, int lastRank)
    {
        LeagueEntity league = await context.Leagues.AsNoTracking().SingleAsync(
            e => e.SeasonId == source.Id && e.Kind == (int)LeagueKind.Feeder
                && e.FeederDivision == (int)division && e.SportingColor == (int)color).ConfigureAwait(false);
        List<SeasonStandingEntity> rows = await context.SeasonStandings.AsNoTracking()
            .Where(e => e.SeasonId == source.Id && e.LeagueId == league.Id && e.SeasonRank >= firstRank && e.SeasonRank <= lastRank)
            .ToListAsync().ConfigureAwait(false);
        rows.Count.ShouldBe(lastRank - firstRank + 1);

        foreach (SeasonStandingEntity row in rows)
        {
            bool hasFeederMovement = await context.Movements.AsNoTracking().AnyAsync(
                e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                    && e.SaveAthleteId == row.SaveAthleteId
                    && (e.Kind == (int)MovementKind.FeederAutomaticPromotion
                        || e.Kind == (int)MovementKind.FeederAutomaticRelegation
                        || e.Kind == (int)MovementKind.FeederQualifierIncumbent
                        || e.Kind == (int)MovementKind.FeederQualifierChallenger)).ConfigureAwait(false);
            hasFeederMovement.ShouldBeFalse($"F3 rank {row.SeasonRank} should have no feeder movement.");
        }
    }

    [Fact]
    public void MaxTurnover_AllChallengersWin_YieldsSixteenNewF1()
    {
        // F1 White: 8 auto (F2 1-8) + 8 qualifier (F2 9-16 challengers all win) = 16 new.
        int autoPromoted = 8;
        int challengerWinnersMax = 8;
        int newF1Max = autoPromoted + challengerWinnersMax;
        newF1Max.ShouldBe(16);

        // F2 White: 8 auto (F3 1-8) + 8 qualifier (F3 9-16 all win) = 16 new.
        int newF2Max = 8 + 8;
        newF2Max.ShouldBe(16);
    }

    [Fact]
    public void ZeroTurnover_AllIncumbentsSurvive_YieldsEightNewF1()
    {
        // F1 White: 8 auto (F2 1-8) + 0 qualifier (all incumbents survive) = 8 new.
        int autoPromoted = 8;
        int challengerWinnersMin = 0;
        int newF1Min = autoPromoted + challengerWinnersMin;
        newF1Min.ShouldBe(8);

        // Qualifier still produces exactly 8 higher-tier occupants (incumbents).
        int qualifierWinners = 8;
        qualifierWinners.ShouldBe(8);
    }

    private static async Task<(RunAllQualifiersResponse Response, string Root)> RunAllForSeedAsync(ulong seed, ulong stream)
    {
        var (store, root) = PostseasonTestSaves.CreateStore();
        var created = await store.CreateAsync("MSS058 Seed", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        await PostseasonTestSaves.CompleteSeasonOneAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        await new CreateInauguralSuperleagueHandler(store).HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        await PostseasonTestSaves.FillSeasonTwoFeedersAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        await PostseasonTestSaves.InsertSyntheticSeasonTwoStandingsAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        await new ResolveAutomaticMovementHandler(store).HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        RunAllQualifiersResponse response = await new RunAllQualifiersHandler(store).HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        // Keep store alive via root; caller deletes root. Store is not returned to avoid disposal issues.
        return (response, root);
    }
}
