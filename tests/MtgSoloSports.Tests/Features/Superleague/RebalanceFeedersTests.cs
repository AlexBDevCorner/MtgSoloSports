using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;
using MtgSoloSports.Features.Superleague.CreateInaugural;
using MtgSoloSports.Features.Superleague.GetRebalanceResult;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Superleague;

public sealed class RebalanceFeedersTests
{
    [Fact]
    public async Task Rebalance_BeforeAnyMovement_Conflicts()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Rebalance Early", 111UL, 222UL, UniverseTestCatalog.Build());
            RebalanceFeedersHandler handler = new(store);
            await Should.ThrowAsync<RebalanceFeedersConflictException>(
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
            SaveStore.CreationRecord created = await store.CreateAsync("Rebalance Missing", 333UL, 444UL, UniverseTestCatalog.Build());
            GetRebalanceResultHandler query = new(store);
            await Should.ThrowAsync<RebalanceResultNotFoundException>(
                () => query.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rebalance_Inaugural_Fills28To32_PreservesHistory()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Rebalance Inaugural", 707UL, 808UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);

            await AssertProvisionalAsync(store, created.Detail.SaveId, 2, 28);

            (int stages, int seasons, int rounds, int qualifierRounds, int qualifierStandings, ulong rngBefore) =
                await CapturePreservationAsync(store, created.Detail.SaveId);

            RebalanceFeedersHandler handler = new(store);
            RebalanceFeedersResponse response = await handler.HandleAsync(created.Detail.SaveId);

            response.FromSeasonNumber.ShouldBe(1);
            response.ToSeasonNumber.ShouldBe(2);
            response.Colors.Count.ShouldBe(24);
            response.Colors.Count(c => c.ProvisionalCount == 28).ShouldBe(8);
            response.Colors.Count(c => c.ProvisionalCount == 32).ShouldBe(16);
            foreach (RebalanceColorResult color in response.Colors.Where(c => c.ProvisionalCount == 28))
            {
                color.DisplacedCount.ShouldBe(0);
                color.DrawnCount.ShouldBe(4);
                color.FinalCount.ShouldBe(32);
            }

            foreach (RebalanceColorResult color in response.Colors.Where(c => c.ProvisionalCount == 32))
            {
                color.DisplacedCount.ShouldBe(0);
                color.DrawnCount.ShouldBe(0);
                color.FinalCount.ShouldBe(32);
            }

            response.TotalDrawn.ShouldBe(32);
            response.TotalDisplaced.ShouldBe(0);
            response.MovementCount.ShouldBe(32);
            response.Draws.Count.ShouldBe(32);
            response.Displaced.Count.ShouldBe(0);
            response.RngAfterState.ShouldNotBe(response.RngBeforeState);

            await AssertFinalAsync(store, created.Detail.SaveId, response);
            await AssertPreservationAsync(store, created.Detail.SaveId, stages, seasons, rounds, qualifierRounds, qualifierStandings, rngBefore, drew: true);
            await AssertQueryMatchesAsync(store, created.Detail.SaveId, response);

            await Should.ThrowAsync<RebalanceFeedersConflictException>(
                () => handler.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rebalance_Normal_WithWhiteCluster_Restores32()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Rebalance Normal", 9001UL, 7002UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);

            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);

            RebalanceFeedersHandler rebalance = new(store);
            RebalanceFeedersResponse inauguralRebalance = await rebalance.HandleAsync(created.Detail.SaveId);
            inauguralRebalance.TotalDrawn.ShouldBe(32);

            await InsertSyntheticSeasonTwoStandingsAsync(store, created.Detail.SaveId, placeWhiteLast: true);

            ResolveAutomaticMovementHandler movement = new(store);
            await movement.HandleAsync(created.Detail.SaveId);

            await Should.ThrowAsync<RebalanceFeedersConflictException>(
                () => rebalance.HandleAsync(created.Detail.SaveId));

            RunQualifierHandler qualifier = new(store);
            RunQualifierResponse qualifierResponse = await qualifier.HandleAsync(created.Detail.SaveId);
            qualifierResponse.Winners.ShouldBe(8);

            Dictionary<int, int> provisional = await LoadFeederCountsAsync(store, created.Detail.SaveId, 3);
            provisional.Values.Sum().ShouldBe(768);

            (int stages, int seasons, int rounds, int qualifierRounds, int qualifierStandings, ulong rngBefore) =
                await CapturePreservationAsync(store, created.Detail.SaveId);

            RebalanceFeedersResponse response = await rebalance.HandleAsync(created.Detail.SaveId);

            response.FromSeasonNumber.ShouldBe(2);
            response.ToSeasonNumber.ShouldBe(3);
            response.Colors.Count.ShouldBe(24);
            foreach (RebalanceColorResult color in response.Colors)
            {
                color.FinalCount.ShouldBe(32);
                (color.DrawnCount == 0 || color.DisplacedCount == 0).ShouldBeTrue();
                color.FinalCount.ShouldBe(color.ProvisionalCount - color.DisplacedCount + color.DrawnCount);
            }

            int expectedMovements = response.TotalDrawn + response.TotalDisplaced;
            response.MovementCount.ShouldBe(expectedMovements);

            await AssertFinalAsync(store, created.Detail.SaveId, response);
            await AssertDisplacedAreLowestAsync(store, created.Detail.SaveId, response);
            await AssertPreservationAsync(store, created.Detail.SaveId, stages, seasons, rounds, qualifierRounds, qualifierStandings, rngBefore, drew: response.TotalDrawn > 0);
            await AssertQueryMatchesAsync(store, created.Detail.SaveId, response);

            await Should.ThrowAsync<RebalanceFeedersConflictException>(
                () => rebalance.HandleAsync(created.Detail.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rebalance_SameSeed_Inaugural_IsDeterministic()
    {
        (RebalanceFeedersResponse first, string rootFirst) = await RunInauguralRebalanceAsync(4242UL, 777UL);
        (RebalanceFeedersResponse second, string rootSecond) = await RunInauguralRebalanceAsync(4242UL, 777UL);
        try
        {
            first.TotalDrawn.ShouldBe(second.TotalDrawn);
            first.Draws.Select(m => m.AthleteId).OrderBy(id => id).ShouldBe(second.Draws.Select(m => m.AthleteId).OrderBy(id => id).ToList());
            first.RngBeforeState.ShouldBe(second.RngBeforeState);
            first.RngAfterState.ShouldBe(second.RngAfterState);
            first.Colors.Select(c => c.DrawnCount).ShouldBe(second.Colors.Select(c => c.DrawnCount).ToList());
        }
        finally
        {
            Directory.Delete(rootFirst, recursive: true);
            Directory.Delete(rootSecond, recursive: true);
        }
    }

    [Fact]
    public void Selection_Overflow_DisplacesLowestRanked()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<RebalanceFeedersSelection.ProvisionalMember> provisional = [];
        for (int rank = 1; rank <= 32; rank++)
        {
            provisional.Add(new RebalanceFeedersSelection.ProvisionalMember(1000 + rank, $"White Athlete {rank:D4}", true, rank));
        }

        for (int i = 0; i < 6; i++)
        {
            provisional.Add(new RebalanceFeedersSelection.ProvisionalMember(2000 + i, $"Returnee {i:D4}", false, 0));
        }

        List<RebalanceFeedersSelection.PoolCandidate> pool = [];
        for (int i = 0; i < 10; i++)
        {
            pool.Add(new RebalanceFeedersSelection.PoolCandidate(3000 + i, $"Pool {i:D4}", SportingColor.White));
        }

        List<RebalanceFeedersSelection.ColorInput> inputs = [new RebalanceFeedersSelection.ColorInput(SportingColor.White, provisional, pool)];
        foreach (SportingColor color in Enum.GetValues<SportingColor>().Where(c => c != SportingColor.White))
        {
            List<RebalanceFeedersSelection.ProvisionalMember> balanced = Enumerable.Range(1, 32)
                .Select(rank => new RebalanceFeedersSelection.ProvisionalMember(
                    8000 + ((int)color * 100) + rank, $"{color} Athlete {rank:D4}", true, rank))
                .ToList();
            inputs.Add(new RebalanceFeedersSelection.ColorInput(color, balanced, []));
        }

        Pcg32V1 rng = new(1UL, 2UL);
        RebalanceFeedersSelection.RebalancePlan plan = RebalanceFeedersSelection.Select(inputs, rng, rules);

        plan.PerColor.Count.ShouldBe(8);
        RebalanceFeedersSelection.ColorPlan white = plan.PerColor.Single(p => p.Color == SportingColor.White);
        white.ProvisionalCount.ShouldBe(38);
        white.Displaced.Count.ShouldBe(6);
        white.Draws.Count.ShouldBe(0);
        white.Displaced.Select(d => d.SourceRank).OrderByDescending(r => r).ShouldBe([32, 31, 30, 29, 28, 27]);

        foreach (RebalanceFeedersSelection.ColorPlan other in plan.PerColor.Where(p => p.Color != SportingColor.White))
        {
            other.ProvisionalCount.ShouldBe(32);
            other.Draws.Count.ShouldBe(0);
            other.Displaced.Count.ShouldBe(0);
        }
    }

    [Fact]
    public void Selection_Underflow_DrawsEqualProbability_Deterministic()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<RebalanceFeedersSelection.PoolCandidate> pool = [];
        for (int i = 0; i < 20; i++)
        {
            pool.Add(new RebalanceFeedersSelection.PoolCandidate(5000 + i, $"Pool Athlete {i:D4}", SportingColor.Blue));
        }

        List<RebalanceFeedersSelection.ProvisionalMember> provisional = [];
        for (int rank = 5; rank <= 32; rank++)
        {
            provisional.Add(new RebalanceFeedersSelection.ProvisionalMember(6000 + rank, $"Blue Athlete {rank:D4}", true, rank));
        }

        RebalanceFeedersSelection.ColorInput blue = new(SportingColor.Blue, provisional, pool);
        List<RebalanceFeedersSelection.ColorInput> others = Enum.GetValues<SportingColor>()
            .Where(c => c != SportingColor.Blue)
            .Select(c => new RebalanceFeedersSelection.ColorInput(
                c,
                Enumerable.Range(1, 32).Select(r => new RebalanceFeedersSelection.ProvisionalMember(
                    7000 + ((int)c * 100) + r, $"{c} Athlete {r:D4}", true, r)).ToList(),
                new List<RebalanceFeedersSelection.PoolCandidate>()))
            .ToList();
        List<RebalanceFeedersSelection.ColorInput> inputs = [blue, .. others];

        Pcg32V1 firstRng = new(99UL, 101UL);
        RebalanceFeedersSelection.RebalancePlan first = RebalanceFeedersSelection.Select(inputs, firstRng, rules);
        Pcg32V1 secondRng = new(99UL, 101UL);
        RebalanceFeedersSelection.RebalancePlan second = RebalanceFeedersSelection.Select(inputs, secondRng, rules);

        RebalanceFeedersSelection.ColorPlan firstBlue = first.PerColor.Single(p => p.Color == SportingColor.Blue);
        RebalanceFeedersSelection.ColorPlan secondBlue = second.PerColor.Single(p => p.Color == SportingColor.Blue);
        firstBlue.Draws.Count.ShouldBe(4);
        firstBlue.Draws.Select(d => d.SaveAthleteId).OrderBy(id => id)
            .ShouldBe(secondBlue.Draws.Select(d => d.SaveAthleteId).OrderBy(id => id).ToList());

        HashSet<int> poolIds = pool.Select(p => p.SaveAthleteId).ToHashSet();
        foreach (RebalanceFeedersSelection.PoolCandidate draw in firstBlue.Draws)
        {
            poolIds.Contains(draw.SaveAthleteId).ShouldBeTrue();
        }
    }

    private static async Task AssertProvisionalAsync(SaveStore store, Guid saveId, int seasonNumber, int expectedPerFeeder)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        List<LeagueEntity> feeders = await context.Leagues.AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.Kind == (int)LeagueKind.Feeder).ToListAsync().ConfigureAwait(false);
        feeders.Count.ShouldBe(24);
        foreach (LeagueEntity feeder in feeders)
        {
            int count = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == season.Id && e.LeagueId == feeder.Id).ConfigureAwait(false);
            int expected = feeder.FeederDivision == (int)FeederDivision.First
                ? expectedPerFeeder
                : 32;
            count.ShouldBe(expected);
        }
    }

    private static async Task AssertFinalAsync(SaveStore store, Guid saveId, RebalanceFeedersResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.ToSeasonNumber).ConfigureAwait(false);
        List<LeagueEntity> feeders = await context.Leagues.AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder).ToListAsync().ConfigureAwait(false);
        LeagueEntity superleague = await context.Leagues.AsNoTracking()
            .SingleAsync(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Superleague).ConfigureAwait(false);

        foreach (LeagueEntity feeder in feeders)
        {
            int count = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == feeder.Id).ConfigureAwait(false);
            count.ShouldBe(32);
        }

        int superCount = await context.SeasonMemberships
            .CountAsync(e => e.SeasonId == next.Id && e.LeagueId == superleague.Id).ConfigureAwait(false);
        superCount.ShouldBe(32);

        List<SeasonMembershipEntity> memberships = await context.SeasonMemberships.AsNoTracking()
            .Where(e => e.SeasonId == next.Id).ToListAsync().ConfigureAwait(false);
        memberships.Count.ShouldBe(2048);

        HashSet<int> active = memberships.Where(m => m.LeagueId is not null).Select(m => m.SaveAthleteId).ToHashSet();
        HashSet<int> pool = memberships.Where(m => m.LeagueId is null).Select(m => m.SaveAthleteId).ToHashSet();
        active.Count.ShouldBe(800);
        pool.Count.ShouldBe(1248);
        active.Intersect(pool).ShouldBeEmpty();
        memberships.Select(m => m.SaveAthleteId).Distinct().Count().ShouldBe(2048);

        Dictionary<int, int> colorByLeague = feeders.ToDictionary(l => l.Id, l => l.SportingColor);
        foreach (SeasonMembershipEntity membership in memberships.Where(m => m.LeagueId is not null && m.LeagueId != superleague.Id))
        {
            colorByLeague[membership.LeagueId!.Value].ShouldBe(membership.SportingColor);
        }

        List<MovementEntity> movements = await context.Movements.AsNoTracking()
            .Where(e => e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement))
            .ToListAsync().ConfigureAwait(false);
        movements.Count.ShouldBe(response.MovementCount);
        movements.Count.ShouldBe(response.TotalDrawn + response.TotalDisplaced);
    }

    private static async Task AssertDisplacedAreLowestAsync(SaveStore store, Guid saveId, RebalanceFeedersResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.FromSeasonNumber).ConfigureAwait(false);

        List<MovementEntity> displaced = await context.Movements.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id
                && e.Kind == (int)MovementKind.RebalanceDisplacement)
            .ToListAsync().ConfigureAwait(false);

        foreach (MovementEntity movement in displaced)
        {
            movement.FromSeasonRank.ShouldBeInRange(1, 32);
            movement.ToLeagueId.ShouldBe(RebalanceFeedersHandler.PoolSentinelLeagueId);
        }

        List<MovementEntity> draws = await context.Movements.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.Kind == (int)MovementKind.RebalanceDraw)
            .ToListAsync().ConfigureAwait(false);
        foreach (MovementEntity movement in draws)
        {
            movement.FromLeagueId.ShouldBe(RebalanceFeedersHandler.PoolSentinelLeagueId);
            movement.FromSeasonRank.ShouldBe(0);
        }
    }

    private static async Task<(int Stages, int Seasons, int Rounds, int QualifierRounds, int QualifierStandings, ulong Rng)> CapturePreservationAsync(
        SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int stages = await context.StageStandings.CountAsync().ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync().ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync().ConfigureAwait(false);
        int qualifierRounds = await context.QualifierRounds.CountAsync().ConfigureAwait(false);
        int qualifierStandings = await context.QualifierStandings.CountAsync().ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        unchecked
        {
            return (stages, seasons, rounds, qualifierRounds, qualifierStandings, (ulong)rng.State);
        }
    }

    private static async Task AssertPreservationAsync(
        SaveStore store,
        Guid saveId,
        int stages,
        int seasons,
        int rounds,
        int qualifierRounds,
        int qualifierStandings,
        ulong rngBefore,
        bool drew)
    {
        (int stagesAfter, int seasonsAfter, int roundsAfter, int qualifierRoundsAfter, int qualifierStandingsAfter, ulong rngAfter) =
            await CapturePreservationAsync(store, saveId).ConfigureAwait(false);
        stagesAfter.ShouldBe(stages);
        seasonsAfter.ShouldBe(seasons);
        roundsAfter.ShouldBe(rounds);
        qualifierRoundsAfter.ShouldBe(qualifierRounds);
        qualifierStandingsAfter.ShouldBe(qualifierStandings);
        if (drew)
        {
            rngAfter.ShouldNotBe(rngBefore);
        }
        else
        {
            rngAfter.ShouldBe(rngBefore);
        }
    }

    [Fact]
    public async Task Rebalance_Inaugural_ExposesDeparturesForReveal()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Rebalance Reveal", 5151UL, 6161UL, UniverseTestCatalog.Build());
            await CompleteSeasonOneAsync(store, created.Detail.SaveId);
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(created.Detail.SaveId);
            RebalanceFeedersHandler handler = new(store);
            RebalanceFeedersResponse response = await handler.HandleAsync(created.Detail.SaveId);

            // Underfilled multi-league case: every feeder loses 4 to Superleague, draws 4.
            response.TotalDeparted.ShouldBe(32);
            response.TotalReturned.ShouldBe(0);
            response.Departed.Count.ShouldBe(32);
            response.Returned.Count.ShouldBe(0);
            foreach (RebalanceColorResult color in response.Colors)
            {
                color.StartingCount.ShouldBe(32);
                color.DepartedCount.ShouldBe(4);
                color.ReturnedCount.ShouldBe(0);
                color.ProvisionalCount.ShouldBe(28);
                color.ProvisionalCount.ShouldBe(
                    color.StartingCount - color.DepartedCount + color.ReturnedCount);
                color.FinalCount.ShouldBe(32);
            }

            foreach (RebalanceMovementMember member in response.Departed)
            {
                member.Kind.ShouldBe(RebalanceSuperleagueTransfers.DepartureKind);
                member.ToLeagueName.ShouldBe("Superleague");
                member.FromSeasonRank.ShouldBeInRange(1, 4);
            }

            foreach (RebalanceMovementMember member in response.Draws)
            {
                member.Kind.ShouldBe("RebalanceDraw");
                member.FromLeagueName.ShouldBe("Common Pool");
                member.FromSeasonRank.ShouldBe(0);
            }

            // Historical replay shows the exact same departures and draws.
            GetRebalanceResultHandler query = new(store);
            GetRebalanceResultResponse replay =
                await query.HandleAsync(created.Detail.SaveId, fromSeasonNumber: 1);
            replay.Departed.Select(m => m.AthleteId).OrderBy(id => id).ShouldBe(
                response.Departed.Select(m => m.AthleteId).OrderBy(id => id).ToList());
            replay.Draws.Select(m => m.AthleteId).OrderBy(id => id).ShouldBe(
                response.Draws.Select(m => m.AthleteId).OrderBy(id => id).ToList());
            replay.Colors.Select(c => c.ProvisionalCount).ShouldBe(
                response.Colors.Select(c => c.ProvisionalCount).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertQueryMatchesAsync(SaveStore store, Guid saveId, RebalanceFeedersResponse response)
    {
        GetRebalanceResultHandler query = new(store);
        GetRebalanceResultResponse summary = await query.HandleAsync(saveId).ConfigureAwait(false);
        summary.FromSeasonNumber.ShouldBe(response.FromSeasonNumber);
        summary.ToSeasonNumber.ShouldBe(response.ToSeasonNumber);
        summary.MovementCount.ShouldBe(response.MovementCount);
        summary.TotalDrawn.ShouldBe(response.TotalDrawn);
        summary.TotalDisplaced.ShouldBe(response.TotalDisplaced);
        summary.TotalDeparted.ShouldBe(response.TotalDeparted);
        summary.TotalReturned.ShouldBe(response.TotalReturned);
        summary.Draws.Select(m => m.AthleteId).OrderBy(id => id)
            .ShouldBe(response.Draws.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        summary.Displaced.Select(m => m.AthleteId).OrderBy(id => id)
            .ShouldBe(response.Displaced.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        summary.Departed.Select(m => m.AthleteId).OrderBy(id => id)
            .ShouldBe(response.Departed.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        summary.Returned.Select(m => m.AthleteId).OrderBy(id => id)
            .ShouldBe(response.Returned.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        foreach (RebalanceColorResult color in summary.Colors)
        {
            color.FinalCount.ShouldBe(32);
            color.ProvisionalCount.ShouldBe(
                color.StartingCount - color.DepartedCount + color.ReturnedCount);
            color.FinalCount.ShouldBe(
                color.ProvisionalCount - color.DisplacedCount + color.DrawnCount);
        }

        GetRebalanceResultResponse again = await query.HandleAsync(saveId, fromSeasonNumber: response.FromSeasonNumber).ConfigureAwait(false);
        again.MovementCount.ShouldBe(response.MovementCount);
        again.TotalDeparted.ShouldBe(response.TotalDeparted);
    }

    private static async Task<(RebalanceFeedersResponse Response, string Root)> RunInauguralRebalanceAsync(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Rebalance Det", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        await CompleteSeasonOneAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        CreateInauguralSuperleagueHandler inaugural = new(store);
        await inaugural.HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        RebalanceFeedersHandler handler = new(store);
        RebalanceFeedersResponse response = await handler.HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        return (response, root);
    }

    private static async Task<Dictionary<int, int>> LoadFeederCountsAsync(SaveStore store, Guid saveId, int seasonNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == seasonNumber).ConfigureAwait(false);
        List<LeagueEntity> feeders = await context.Leagues.AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.Kind == (int)LeagueKind.Feeder).ToListAsync().ConfigureAwait(false);
        Dictionary<int, int> counts = new();
        foreach (LeagueEntity feeder in feeders)
        {
            int count = await context.SeasonMemberships
                .CountAsync(e => e.SeasonId == season.Id && e.LeagueId == feeder.Id).ConfigureAwait(false);
            counts[feeder.Id] = count;
        }

        return counts;
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

    private static async Task InsertSyntheticSeasonTwoStandingsAsync(SaveStore store, Guid saveId, bool placeWhiteLast)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity seasonTwo = await context.Seasons.SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues
            .Where(e => e.SeasonId == seasonTwo.Id)
            .OrderBy(e => e.Id)
            .ToListAsync().ConfigureAwait(false);
        leagues.Count.ShouldBe(9);

        foreach (LeagueEntity league in leagues)
        {
            List<SeasonMembershipEntity> memberships = await context.SeasonMemberships
                .Where(e => e.SeasonId == seasonTwo.Id && e.LeagueId == league.Id)
                .ToListAsync().ConfigureAwait(false);
            memberships.Count.ShouldBe(32);

            List<SeasonMembershipEntity> ordered;
            if (league.Kind == (int)LeagueKind.Superleague && placeWhiteLast)
            {
                ordered = memberships
                    .OrderBy(m => m.SportingColor == (int)SportingColor.White ? 1 : 0)
                    .ThenBy(m => m.SaveAthleteId)
                    .ToList();
            }
            else
            {
                ordered = memberships.OrderBy(m => m.SaveAthleteId).ToList();
            }

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

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-rebalance-" + Guid.NewGuid().ToString("N"));
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
