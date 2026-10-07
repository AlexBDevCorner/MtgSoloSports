using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Qualifiers;
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
        // MSS-067: shared Season 1 template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-rebalance-");
        try
        {
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(saveId);

            await AssertProvisionalAsync(store, saveId, 2, 28);

            (int stages, int seasons, int rounds, int qualifierRounds, int qualifierStandings, ulong rngBefore) =
                await CapturePreservationAsync(store, saveId);

            RebalanceFeedersHandler handler = new(store);
            RebalanceFeedersResponse response = await handler.HandleAsync(saveId);

            response.FromSeasonNumber.ShouldBe(1);
            response.ToSeasonNumber.ShouldBe(2);
            AssertInauguralCascadeCounts(response);

            response.TotalDrawn.ShouldBe(32);
            response.TotalDisplaced.ShouldBe(0);
            response.TotalRebalancedUp.ShouldBe(64);
            response.TotalRebalancedDown.ShouldBe(0);
            response.MovementCount.ShouldBe(96);
            response.Draws.Count.ShouldBe(32);
            response.Displaced.Count.ShouldBe(0);
            response.RebalancedUp.Count.ShouldBe(64);
            response.RebalancedDown.Count.ShouldBe(0);
            response.RngAfterState.ShouldNotBe(response.RngBeforeState);

            await AssertFinalAsync(store, saveId, response);
            await AssertNoPoolBypassAsync(store, saveId, response);
            await AssertPreservationAsync(store, saveId, stages, seasons, rounds, qualifierRounds, qualifierStandings, rngBefore, drew: true);
            await AssertQueryMatchesAsync(store, saveId, response);

            await Should.ThrowAsync<RebalanceFeedersConflictException>(
                () => handler.HandleAsync(saveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rebalance_Normal_WithWhiteCluster_Restores32()
    {
        // MSS-067: shared Season 1 template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-rebalance-");
        try
        {
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(saveId);

            RebalanceFeedersHandler rebalance = new(store);
            RebalanceFeedersResponse inauguralRebalance = await rebalance.HandleAsync(saveId);
            inauguralRebalance.TotalDrawn.ShouldBe(32);

            await InsertSyntheticSeasonTwoStandingsAsync(store, saveId, placeWhiteLast: true);

            ResolveAutomaticMovementHandler movement = new(store);
            await movement.HandleAsync(saveId);

            await Should.ThrowAsync<RebalanceFeedersConflictException>(
                () => rebalance.HandleAsync(saveId));

            RunAllQualifiersHandler qualifier = new(store);
            RunAllQualifiersResponse qualifierResponse = await qualifier.HandleAsync(saveId);
            qualifierResponse.TotalStandings.ShouldBe(288);
            qualifierResponse.ExecutedNow.Count.ShouldBe(17);

            Dictionary<int, int> provisional = await LoadFeederCountsAsync(store, saveId, 3);
            provisional.Values.Sum().ShouldBe(768);

            (int stages, int seasons, int rounds, int qualifierRounds, int qualifierStandings, ulong rngBefore) =
                await CapturePreservationAsync(store, saveId);

            RebalanceFeedersResponse response = await rebalance.HandleAsync(saveId);

            response.FromSeasonNumber.ShouldBe(2);
            response.ToSeasonNumber.ShouldBe(3);
            AssertNormalCascadeCounts(response);

            int expectedMovements = response.TotalDrawn + response.TotalDisplaced
                + response.TotalRebalancedUp + response.TotalRebalancedDown;
            response.MovementCount.ShouldBe(expectedMovements);

            await AssertFinalAsync(store, saveId, response);
            await AssertDisplacedAreLowestAsync(store, saveId, response);
            await AssertNoPoolBypassAsync(store, saveId, response);
            await AssertPreservationAsync(store, saveId, stages, seasons, rounds, qualifierRounds, qualifierStandings, rngBefore, drew: response.TotalDrawn > 0);
            await AssertQueryMatchesAsync(store, saveId, response);

            await Should.ThrowAsync<RebalanceFeedersConflictException>(
                () => rebalance.HandleAsync(saveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rebalance_SameSeed_Inaugural_IsDeterministic()
    {
        // MSS-067: one prepared save forked into two isolated copies instead of
        // simulating the same Season 1 twice; both runs still execute
        // independently from bit-identical starting state.
        var (store, root, saveId) = await PrepareInauguralForSeedAsync(4242UL, 777UL);
        var (secondStore, secondRoot, secondId) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-rebalance-det-");
        try
        {
            RebalanceFeedersResponse first = await new RebalanceFeedersHandler(store).HandleAsync(saveId);
            RebalanceFeedersResponse second = await new RebalanceFeedersHandler(secondStore).HandleAsync(secondId);
            first.TotalDrawn.ShouldBe(second.TotalDrawn);
            first.Draws.Select(m => m.AthleteId).OrderBy(id => id).ShouldBe(second.Draws.Select(m => m.AthleteId).OrderBy(id => id).ToList());
            first.RngBeforeState.ShouldBe(second.RngBeforeState);
            first.RngAfterState.ShouldBe(second.RngAfterState);
            first.Colors.Select(c => c.DrawnCount).ShouldBe(second.Colors.Select(c => c.DrawnCount).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            TestSaveStores.DeleteRoot(secondRoot);
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

    [Fact]
    public void Selection_TierCascade_ShortagePullsBestRetainedThroughF2F3OnlyF3Draws()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        // White: F1 28 (short 4), F2 32 retained ranks 1..32, F3 32 retained, pool 10.
        List<RebalanceFeedersSelection.TierMember> f1 = TierMembers(1000, "White F1", 1, 28, isProtected: false);
        List<RebalanceFeedersSelection.TierMember> f2 = TierMembers(2000, "White F2", 1, 32, isProtected: false);
        List<RebalanceFeedersSelection.TierMember> f3 = TierMembers(3000, "White F3", 1, 32, isProtected: false);
        List<RebalanceFeedersSelection.PoolCandidate> pool = TierPool(SportingColor.White, 4000, 10);

        List<RebalanceFeedersSelection.TierColorInput> inputs = TierInputs(
            new RebalanceFeedersSelection.TierColorInput(SportingColor.White, f1, f2, f3, pool), rules);

        Pcg32V1 rng = new(7UL, 11UL);
        RebalanceFeedersSelection.RebalancePlan plan = RebalanceFeedersSelection.Select(inputs, rng, rules);

        RebalanceFeedersSelection.TierColorPlan white = plan.TierPerColor.Single(p => p.Color == SportingColor.White);
        white.F1ProvisionalCount.ShouldBe(28);
        white.F2ProvisionalCount.ShouldBe(32);
        white.F3ProvisionalCount.ShouldBe(32);
        // F1 pulls best 4 retained F2 (ranks 1..4).
        white.UpMoves.Count(m => m.FromDivision == FeederDivision.Second && m.ToDivision == FeederDivision.First).ShouldBe(4);
        white.UpMoves.Where(m => m.FromDivision == FeederDivision.Second)
            .Select(m => m.SourceRank).OrderBy(r => r).ShouldBe([1, 2, 3, 4]);
        // F2 pulls best 4 retained F3.
        white.UpMoves.Count(m => m.FromDivision == FeederDivision.Third && m.ToDivision == FeederDivision.Second).ShouldBe(4);
        white.UpMoves.Where(m => m.FromDivision == FeederDivision.Third)
            .Select(m => m.SourceRank).OrderBy(r => r).ShouldBe([1, 2, 3, 4]);
        white.DownMoves.Count.ShouldBe(0);
        // Only F3 draws from pool.
        white.Draws.Count.ShouldBe(4);
        white.Displaced.Count.ShouldBe(0);
        plan.AllUpMoves.Count.ShouldBe(8);
        plan.AllDraws.Count.ShouldBe(4);
    }

    [Fact]
    public void Selection_TierCascade_OverflowPushesWorstRetainedOnlyF3Displaces()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        // Blue: F1 36 (32 retained ranks 1..32 + 4 protected returnees), F2/F3 balanced.
        List<RebalanceFeedersSelection.TierMember> f1 = TierMembers(5000, "Blue F1", 1, 32, isProtected: false);
        f1.AddRange(TierMembers(5900, "Blue Returnee", 1, 4, isProtected: true));
        List<RebalanceFeedersSelection.TierMember> f2 = TierMembers(6000, "Blue F2", 1, 32, isProtected: false);
        List<RebalanceFeedersSelection.TierMember> f3 = TierMembers(7000, "Blue F3", 1, 32, isProtected: false);
        List<RebalanceFeedersSelection.PoolCandidate> pool = TierPool(SportingColor.Blue, 8000, 10);

        List<RebalanceFeedersSelection.TierColorInput> inputs = TierInputs(
            new RebalanceFeedersSelection.TierColorInput(SportingColor.Blue, f1, f2, f3, pool), rules);

        Pcg32V1 rng = new(21UL, 22UL);
        RebalanceFeedersSelection.RebalancePlan plan = RebalanceFeedersSelection.Select(inputs, rng, rules);

        RebalanceFeedersSelection.TierColorPlan blue = plan.TierPerColor.Single(p => p.Color == SportingColor.Blue);
        blue.F1ProvisionalCount.ShouldBe(36);
        // F1 pushes worst 4 retained (ranks 32,31,30,29), never protected returnees.
        blue.DownMoves.Count(m => m.FromDivision == FeederDivision.First).ShouldBe(4);
        blue.DownMoves.Where(m => m.FromDivision == FeederDivision.First)
            .Select(m => m.SourceRank).OrderByDescending(r => r).ShouldBe([32, 31, 30, 29]);
        blue.DownMoves.Where(m => m.FromDivision == FeederDivision.First).All(m => m.SaveAthleteId < 5900).ShouldBeTrue();
        // F2 overflow pushes worst retained F2 to F3 (excluding F1 arrivals).
        blue.DownMoves.Count(m => m.FromDivision == FeederDivision.Second).ShouldBe(4);
        blue.UpMoves.Count.ShouldBe(0);
        blue.Draws.Count.ShouldBe(0);
        blue.Displaced.Count.ShouldBe(4);
        plan.AllDownMoves.Count.ShouldBe(8);
        plan.AllDisplaced.Count.ShouldBe(4);
    }

    [Fact]
    public void Selection_TierCascade_ProtectsNewlyPromotedFromDown()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        // F1 33: 32 retained + 1 newly promoted protected (rank 1, best).
        // Overflow 1 must displace worst retained (rank 32), not the protected rank-1 newcomer.
        List<RebalanceFeedersSelection.TierMember> f1 = TierMembers(9000, "Green F1", 1, 32, isProtected: false);
        f1.Add(new RebalanceFeedersSelection.TierMember(9999, "Green Newcomer", 1, true));
        List<RebalanceFeedersSelection.TierMember> f2 = TierMembers(9100, "Green F2", 1, 32, isProtected: false);
        List<RebalanceFeedersSelection.TierMember> f3 = TierMembers(9200, "Green F3", 1, 32, isProtected: false);

        List<RebalanceFeedersSelection.TierColorInput> inputs = TierInputs(
            new RebalanceFeedersSelection.TierColorInput(SportingColor.Green, f1, f2, f3, Pool: TierPool(SportingColor.Green, 9300, 10)), rules);

        RebalanceFeedersSelection.RebalancePlan plan = RebalanceFeedersSelection.Select(inputs, new Pcg32V1(3UL, 4UL), rules);
        RebalanceFeedersSelection.TierColorPlan green = plan.TierPerColor.Single(p => p.Color == SportingColor.Green);
        green.DownMoves.Count(m => m.FromDivision == FeederDivision.First).ShouldBe(1);
        green.DownMoves.Single(m => m.FromDivision == FeederDivision.First).SaveAthleteId.ShouldNotBe(9999);
        green.DownMoves.Single(m => m.FromDivision == FeederDivision.First).SourceRank.ShouldBe(32);
    }

    [Fact]
    public void Selection_TierCascade_ProtectsNewlyRelegatedFromUp()
    {
        // F2 shortage protection: F2 has 1 newly relegated protected (rank 32, worst)
        // plus retained ranks 1..31. Shortage 1 must pull best retained (rank 1),
        // never the protected relegated athlete.
        RulesV1 rules = RulesV1.CreateDefault();
        List<RebalanceFeedersSelection.TierMember> h1 = TierMembers(9400, "Red F1", 1, 31, isProtected: false);
        List<RebalanceFeedersSelection.TierMember> h2 = TierMembers(9500, "Red F2", 1, 31, isProtected: false);
        h2.Add(new RebalanceFeedersSelection.TierMember(9599, "Red Relegated", 32, true));
        List<RebalanceFeedersSelection.TierMember> h3 = TierMembers(9600, "Red F3", 1, 32, isProtected: false);
        List<RebalanceFeedersSelection.TierColorInput> redInputs = TierInputs(
            new RebalanceFeedersSelection.TierColorInput(SportingColor.Red, h1, h2, h3, TierPool(SportingColor.Red, 9700, 10)), rules);
        RebalanceFeedersSelection.RebalancePlan redPlan = RebalanceFeedersSelection.Select(redInputs, new Pcg32V1(5UL, 6UL), rules);
        RebalanceFeedersSelection.TierColorPlan red = redPlan.TierPerColor.Single(p => p.Color == SportingColor.Red);
        red.UpMoves.Single(m => m.FromDivision == FeederDivision.Second).SaveAthleteId.ShouldNotBe(9599);
        red.UpMoves.Single(m => m.FromDivision == FeederDivision.Second).SourceRank.ShouldBe(1);
    }

    [Fact]
    public void Selection_TierCascade_NoColorChange_NeedsNoMovesOrRng()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<RebalanceFeedersSelection.TierColorInput> inputs = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            inputs.Add(new RebalanceFeedersSelection.TierColorInput(
                color,
                TierMembers(10000 + ((int)color * 1000), $"{color} F1", 1, 32, isProtected: false),
                TierMembers(20000 + ((int)color * 1000), $"{color} F2", 1, 32, isProtected: false),
                TierMembers(30000 + ((int)color * 1000), $"{color} F3", 1, 32, isProtected: false),
                []));
        }

        Pcg32V1 rng = new(11UL, 12UL);
        Pcg32State before = rng.Snapshot();
        RebalanceFeedersSelection.RebalancePlan plan = RebalanceFeedersSelection.Select(inputs, rng, rules);
        plan.AllUpMoves.Count.ShouldBe(0);
        plan.AllDownMoves.Count.ShouldBe(0);
        plan.AllDraws.Count.ShouldBe(0);
        plan.AllDisplaced.Count.ShouldBe(0);
        rng.Snapshot().State.ShouldBe(before.State);
        foreach (RebalanceFeedersSelection.TierColorPlan colorPlan in plan.TierPerColor)
        {
            colorPlan.F1ProvisionalCount.ShouldBe(32);
            colorPlan.F2ProvisionalCount.ShouldBe(32);
            colorPlan.F3ProvisionalCount.ShouldBe(32);
        }
    }

    [Fact]
    public void Selection_TierCascade_PoolOnlyConnectsToF3_DeterministicRngOrder()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<RebalanceFeedersSelection.TierColorInput> inputs = BuildMixedShortageOverflowInputs();

        RebalanceFeedersSelection.RebalancePlan first = RebalanceFeedersSelection.Select(inputs, new Pcg32V1(77UL, 78UL), rules);
        RebalanceFeedersSelection.RebalancePlan second = RebalanceFeedersSelection.Select(inputs, new Pcg32V1(77UL, 78UL), rules);
        first.AllDraws.Select(d => d.SaveAthleteId).OrderBy(id => id)
            .ShouldBe(second.AllDraws.Select(d => d.SaveAthleteId).OrderBy(id => id).ToList());
        first.AllDisplaced.Select(d => d.SaveAthleteId).OrderBy(id => id)
            .ShouldBe(second.AllDisplaced.Select(d => d.SaveAthleteId).OrderBy(id => id).ToList());

        AssertPoolBoundaryOnlyF3(first);
        AssertMixedCounts(first);
    }

    private static List<RebalanceFeedersSelection.TierColorInput> BuildMixedShortageOverflowInputs()
    {
        // White shortage 2, Blue overflow 2, others balanced.
        List<RebalanceFeedersSelection.TierColorInput> inputs = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            int baseId = 40000 + ((int)color * 1000);
            if (color == SportingColor.White)
            {
                inputs.Add(new RebalanceFeedersSelection.TierColorInput(
                    color,
                    TierMembers(baseId, "White F1", 1, 30, isProtected: false),
                    TierMembers(baseId + 100, "White F2", 1, 32, isProtected: false),
                    TierMembers(baseId + 200, "White F3", 1, 32, isProtected: false),
                    TierPool(color, baseId + 300, 10)));
            }
            else if (color == SportingColor.Blue)
            {
                List<RebalanceFeedersSelection.TierMember> f1 = TierMembers(baseId, "Blue F1", 1, 32, isProtected: false);
                f1.AddRange(TierMembers(baseId + 50, "Blue Extra", 20, 2, isProtected: false));
                inputs.Add(new RebalanceFeedersSelection.TierColorInput(
                    color,
                    f1,
                    TierMembers(baseId + 100, "Blue F2", 1, 32, isProtected: false),
                    TierMembers(baseId + 200, "Blue F3", 1, 32, isProtected: false),
                    TierPool(color, baseId + 300, 10)));
            }
            else
            {
                inputs.Add(new RebalanceFeedersSelection.TierColorInput(
                    color,
                    TierMembers(baseId, $"{color} F1", 1, 32, isProtected: false),
                    TierMembers(baseId + 100, $"{color} F2", 1, 32, isProtected: false),
                    TierMembers(baseId + 200, $"{color} F3", 1, 32, isProtected: false),
                    []));
            }
        }

        return inputs;
    }

    private static void AssertPoolBoundaryOnlyF3(RebalanceFeedersSelection.RebalancePlan first)
    {
        // Pool boundary: every draw/displacement touches F3 only; no athlete
        // moves twice; every division restores 32.
        foreach (RebalanceFeedersSelection.TierColorPlan colorPlan in first.TierPerColor)
        {
            if (colorPlan.Draws.Count > 0 || colorPlan.Displaced.Count > 0)
            {
                colorPlan.Color.ShouldBeOneOf(SportingColor.White, SportingColor.Blue);
            }

            HashSet<int> moved = first.AllUpMoves.Concat(first.AllDownMoves).Select(m => m.SaveAthleteId).ToHashSet();
            foreach (RebalanceFeedersSelection.PoolCandidate draw in colorPlan.Draws)
            {
                moved.ShouldNotContain(draw.SaveAthleteId);
            }

            foreach (RebalanceFeedersSelection.RetainedCandidate displaced in colorPlan.Displaced)
            {
                moved.ShouldNotContain(displaced.SaveAthleteId);
            }
        }
    }

    private static void AssertMixedCounts(RebalanceFeedersSelection.RebalancePlan first)
    {
        RebalanceFeedersSelection.TierColorPlan white = first.TierPerColor.Single(p => p.Color == SportingColor.White);
        white.Draws.Count.ShouldBe(2);
        white.Displaced.Count.ShouldBe(0);
        RebalanceFeedersSelection.TierColorPlan blue = first.TierPerColor.Single(p => p.Color == SportingColor.Blue);
        blue.Displaced.Count.ShouldBe(2);
        blue.Draws.Count.ShouldBe(0);
    }

    private static List<RebalanceFeedersSelection.TierMember> TierMembers(int startId, string prefix, int firstRank, int count, bool isProtected)
    {
        List<RebalanceFeedersSelection.TierMember> members = new(count);
        for (int i = 0; i < count; i++)
        {
            int rank = firstRank + i;
            members.Add(new RebalanceFeedersSelection.TierMember(startId + i, $"{prefix} {rank:D4}", rank, isProtected));
        }

        return members;
    }

    private static List<RebalanceFeedersSelection.PoolCandidate> TierPool(SportingColor color, int startId, int count)
    {
        List<RebalanceFeedersSelection.PoolCandidate> pool = new(count);
        for (int i = 0; i < count; i++)
        {
            pool.Add(new RebalanceFeedersSelection.PoolCandidate(startId + i, $"Pool {color} {i:D4}", color));
        }

        return pool;
    }

    private static List<RebalanceFeedersSelection.TierColorInput> TierInputs(
        RebalanceFeedersSelection.TierColorInput focus, RulesV1 rules)
    {
        List<RebalanceFeedersSelection.TierColorInput> inputs = [focus];
        foreach (SportingColor color in Enum.GetValues<SportingColor>().Where(c => c != focus.Color))
        {
            int baseId = 60000 + ((int)color * 1000);
            inputs.Add(new RebalanceFeedersSelection.TierColorInput(
                color,
                TierMembers(baseId, $"{color} F1", 1, 32, isProtected: false),
                TierMembers(baseId + 100, $"{color} F2", 1, 32, isProtected: false),
                TierMembers(baseId + 200, $"{color} F3", 1, 32, isProtected: false),
                []));
        }

        return inputs;
    }

    private static void AssertInauguralCascadeCounts(RebalanceFeedersResponse response)
    {
        response.Colors.Count.ShouldBe(24);
        response.Colors.Count(c => c.ProvisionalCount == 28).ShouldBe(8);
        response.Colors.Count(c => c.ProvisionalCount == 32).ShouldBe(16);
        AssertF1Shortage(response);
        AssertF2Through(response);
        AssertF3Draws(response);
    }

    private static void AssertF1Shortage(RebalanceFeedersResponse response)
    {
        foreach (RebalanceColorResult color in response.Colors.Where(c => c.FeederDivision == (int)FeederDivision.First))
        {
            color.ProvisionalCount.ShouldBe(28);
            color.DisplacedCount.ShouldBe(0);
            color.DrawnCount.ShouldBe(0);
            color.RebalancedUpIn.ShouldBe(4);
            color.RebalancedDownOut.ShouldBe(0);
            color.FinalCount.ShouldBe(32);
        }
    }

    private static void AssertF2Through(RebalanceFeedersResponse response)
    {
        foreach (RebalanceColorResult color in response.Colors.Where(c => c.FeederDivision == (int)FeederDivision.Second))
        {
            color.ProvisionalCount.ShouldBe(32);
            color.DisplacedCount.ShouldBe(0);
            color.DrawnCount.ShouldBe(0);
            color.RebalancedUpOut.ShouldBe(4);
            color.RebalancedUpIn.ShouldBe(4);
            color.FinalCount.ShouldBe(32);
        }
    }

    private static void AssertF3Draws(RebalanceFeedersResponse response)
    {
        foreach (RebalanceColorResult color in response.Colors.Where(c => c.FeederDivision == (int)FeederDivision.Third))
        {
            color.ProvisionalCount.ShouldBe(32);
            color.DisplacedCount.ShouldBe(0);
            color.DrawnCount.ShouldBe(4);
            color.RebalancedUpOut.ShouldBe(4);
            color.FinalCount.ShouldBe(32);
        }
    }

    private static void AssertNormalCascadeCounts(RebalanceFeedersResponse response)
    {
        response.Colors.Count.ShouldBe(24);
        foreach (RebalanceColorResult color in response.Colors)
        {
            color.FinalCount.ShouldBe(32);
            (color.DrawnCount == 0 || color.DisplacedCount == 0).ShouldBeTrue();
            if (color.FeederDivision != (int)FeederDivision.Third)
            {
                color.DrawnCount.ShouldBe(0);
                color.DisplacedCount.ShouldBe(0);
            }

            int structuralNet = color.RebalancedUpIn + color.RebalancedDownIn
                - color.RebalancedUpOut - color.RebalancedDownOut;
            int poolNet = color.DrawnCount - color.DisplacedCount;
            color.FinalCount.ShouldBe(color.ProvisionalCount + structuralNet + poolNet);
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
                && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement
                    || e.Kind == (int)MovementKind.RebalanceUp || e.Kind == (int)MovementKind.RebalanceDown))
            .ToListAsync().ConfigureAwait(false);
        movements.Count.ShouldBe(response.MovementCount);
        movements.Count.ShouldBe(
            response.TotalDrawn + response.TotalDisplaced + response.TotalRebalancedUp + response.TotalRebalancedDown);
    }

    private static async Task AssertNoPoolBypassAsync(SaveStore store, Guid saveId, RebalanceFeedersResponse response)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.FromSeasonNumber).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == response.ToSeasonNumber).ConfigureAwait(false);
        List<LeagueEntity> feeders = await context.Leagues.AsNoTracking()
            .Where(e => e.SeasonId == next.Id && e.Kind == (int)LeagueKind.Feeder).ToListAsync().ConfigureAwait(false);
        Dictionary<int, LeagueEntity> byId = feeders.ToDictionary(l => l.Id);

        List<MovementEntity> movements = await context.Movements.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id
                && (e.Kind == (int)MovementKind.RebalanceDraw || e.Kind == (int)MovementKind.RebalanceDisplacement
                    || e.Kind == (int)MovementKind.RebalanceUp || e.Kind == (int)MovementKind.RebalanceDown))
            .ToListAsync().ConfigureAwait(false);

        foreach (MovementEntity movement in movements)
        {
            if (movement.Kind == (int)MovementKind.RebalanceDraw)
            {
                movement.FromLeagueId.ShouldBe(RebalanceFeedersHandler.PoolSentinelLeagueId);
                byId.TryGetValue(movement.ToLeagueId, out LeagueEntity? to).ShouldBeTrue();
                to!.FeederDivision.ShouldBe((int)FeederDivision.Third);
            }
            else if (movement.Kind == (int)MovementKind.RebalanceDisplacement)
            {
                movement.ToLeagueId.ShouldBe(RebalanceFeedersHandler.PoolSentinelLeagueId);
                byId.TryGetValue(movement.FromLeagueId, out LeagueEntity? from).ShouldBeTrue();
                from!.FeederDivision.ShouldBe((int)FeederDivision.Third);
            }
            else
            {
                movement.FromLeagueId.ShouldNotBe(RebalanceFeedersHandler.PoolSentinelLeagueId);
                movement.ToLeagueId.ShouldNotBe(RebalanceFeedersHandler.PoolSentinelLeagueId);
                byId.TryGetValue(movement.FromLeagueId, out LeagueEntity? from).ShouldBeTrue();
                byId.TryGetValue(movement.ToLeagueId, out LeagueEntity? to).ShouldBeTrue();
                from!.SportingColor.ShouldBe(to!.SportingColor);
                Math.Abs(from.FeederDivision - to.FeederDivision).ShouldBe(1);
            }
        }

        // No persisted Pool↔F1/F2 movement exists.
        foreach (MovementEntity movement in movements.Where(m =>
            m.Kind == (int)MovementKind.RebalanceDraw || m.Kind == (int)MovementKind.RebalanceDisplacement))
        {
            if (movement.Kind == (int)MovementKind.RebalanceDraw)
            {
                byId[movement.ToLeagueId].FeederDivision.ShouldBe((int)FeederDivision.Third);
            }
            else
            {
                byId[movement.FromLeagueId].FeederDivision.ShouldBe((int)FeederDivision.Third);
            }
        }
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
        // MSS-067: shared Season 1 template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-rebalance-");
        try
        {
            CreateInauguralSuperleagueHandler inaugural = new(store);
            await inaugural.HandleAsync(saveId);
            RebalanceFeedersHandler handler = new(store);
            RebalanceFeedersResponse response = await handler.HandleAsync(saveId);

            AssertTieredInauguralColors(response);
            AssertDepartedForReveal(response);
            AssertDrawsForReveal(response);

            await AssertReplayMatchesAsync(store, response);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertDepartedForReveal(RebalanceFeedersResponse response)
    {
        foreach (RebalanceMovementMember member in response.Departed)
        {
            member.Kind.ShouldBe(RebalanceSuperleagueTransfers.DepartureKind);
            member.ToLeagueName.ShouldBe("Superleague");
            member.FromSeasonRank.ShouldBeInRange(1, 4);
        }
    }

    private static void AssertDrawsForReveal(RebalanceFeedersResponse response)
    {
        foreach (RebalanceMovementMember member in response.Draws)
        {
            member.Kind.ShouldBe("RebalanceDraw");
            member.FromLeagueName.ShouldBe("Common Pool");
            member.FromSeasonRank.ShouldBe(0);
        }
    }

    private static async Task AssertReplayMatchesAsync(SaveStore store, RebalanceFeedersResponse response)
    {
        // Historical replay shows the exact same departures and draws.
        GetRebalanceResultHandler query = new(store);
        GetRebalanceResultResponse replay =
            await query.HandleAsync(response.SaveId, fromSeasonNumber: 1).ConfigureAwait(false);
        replay.Departed.Select(m => m.AthleteId).OrderBy(id => id).ShouldBe(
            response.Departed.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        replay.Draws.Select(m => m.AthleteId).OrderBy(id => id).ShouldBe(
            response.Draws.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        replay.Colors.Select(c => c.ProvisionalCount).ShouldBe(
            response.Colors.Select(c => c.ProvisionalCount).ToList());
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
        summary.TotalRebalancedUp.ShouldBe(response.TotalRebalancedUp);
        summary.TotalRebalancedDown.ShouldBe(response.TotalRebalancedDown);
        summary.TotalDeparted.ShouldBe(response.TotalDeparted);
        summary.TotalReturned.ShouldBe(response.TotalReturned);
        summary.Draws.Select(m => m.AthleteId).OrderBy(id => id)
            .ShouldBe(response.Draws.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        summary.Displaced.Select(m => m.AthleteId).OrderBy(id => id)
            .ShouldBe(response.Displaced.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        summary.RebalancedUp.Select(m => m.AthleteId).OrderBy(id => id)
            .ShouldBe(response.RebalancedUp.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        summary.RebalancedDown.Select(m => m.AthleteId).OrderBy(id => id)
            .ShouldBe(response.RebalancedDown.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        summary.Departed.Select(m => m.AthleteId).OrderBy(id => id)
            .ShouldBe(response.Departed.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        summary.Returned.Select(m => m.AthleteId).OrderBy(id => id)
            .ShouldBe(response.Returned.Select(m => m.AthleteId).OrderBy(id => id).ToList());
        foreach (RebalanceColorResult color in summary.Colors)
        {
            color.FinalCount.ShouldBe(32);
            int structuralNet = color.RebalancedUpIn + color.RebalancedDownIn
                - color.RebalancedUpOut - color.RebalancedDownOut;
            int poolNet = color.DrawnCount - color.DisplacedCount;
            color.FinalCount.ShouldBe(color.ProvisionalCount + structuralNet + poolNet);
        }

        GetRebalanceResultResponse again = await query.HandleAsync(saveId, fromSeasonNumber: response.FromSeasonNumber).ConfigureAwait(false);
        again.MovementCount.ShouldBe(response.MovementCount);
        again.TotalDeparted.ShouldBe(response.TotalDeparted);
    }

    private static void AssertTieredInauguralColors(RebalanceFeedersResponse response)
    {
        response.TotalDeparted.ShouldBe(32);
        response.TotalReturned.ShouldBe(0);
        response.Departed.Count.ShouldBe(32);
        response.Returned.Count.ShouldBe(0);
        response.Colors.Count.ShouldBe(24);
        response.Colors.Count(c => c.ProvisionalCount == 28).ShouldBe(8);
        response.Colors.Count(c => c.ProvisionalCount == 32).ShouldBe(16);
        foreach (RebalanceColorResult color in response.Colors.Where(c => c.ProvisionalCount == 28))
        {
            color.StartingCount.ShouldBe(32);
            color.DepartedCount.ShouldBe(4);
            color.ReturnedCount.ShouldBe(0);
            color.ProvisionalCount.ShouldBe(28);
            color.FinalCount.ShouldBe(32);
        }

        foreach (RebalanceColorResult color in response.Colors.Where(c => c.ProvisionalCount == 32))
        {
            color.StartingCount.ShouldBe(32);
            color.DepartedCount.ShouldBe(0);
            color.ReturnedCount.ShouldBe(0);
            color.ProvisionalCount.ShouldBe(32);
            color.FinalCount.ShouldBe(32);
        }
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareInauguralForSeedAsync(ulong seed, ulong stream)
    {
        var (store, root) = CreateStore();
        SaveStore.CreationRecord created = await store.CreateAsync("Rebalance Det", seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        await CompleteSeasonOneAsync(store, created.Detail.SaveId).ConfigureAwait(false);
        CreateInauguralSuperleagueHandler inaugural = new(store);
        await inaugural.HandleAsync(created.Detail.SaveId).ConfigureAwait(false);
        return (store, root, created.Detail.SaveId);
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
        leagues.Count.ShouldBe(25);

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
