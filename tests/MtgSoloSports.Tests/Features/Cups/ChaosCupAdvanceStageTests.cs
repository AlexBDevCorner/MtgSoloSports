using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.AdvanceChaosCupStage;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Cups.RunChaosCupGroup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-102: qualify top 16 per group and draw each subsequent Chaos stage.
/// Proves next-stage draws require full completion, carry exactly the advancers
/// with reset scores, stay pure random after stage 0, persist once-only with
/// independent group RNG, and resolve the Final to P1..P32 plus exit-stage
/// categories without fabricated global ranks.
/// </summary>
public sealed class ChaosCupAdvanceStageTests
{
    [Fact]
    public void DrawRandomStage_RejectsStageZero_AndWrongCounts()
    {
        List<AdvanceChaosCupStageSelection.StageAdvancer> advancers = Enumerable.Range(1, 512)
            .Select(id => new AdvanceChaosCupStageSelection.StageAdvancer(id, $"Athlete {id}"))
            .ToList();
        Should.Throw<InvalidOperationException>(() =>
            AdvanceChaosCupStageSelection.DrawRandomStage(advancers, Pcg32V1.Restore(new Pcg32State(1UL, 2UL)), stageIndex: 0));

        List<AdvanceChaosCupStageSelection.StageAdvancer> shortList = advancers.Take(511).ToList();
        Should.Throw<InvalidOperationException>(() =>
            AdvanceChaosCupStageSelection.DrawRandomStage(shortList, Pcg32V1.Restore(new Pcg32State(11UL, 12UL)), stageIndex: 1));
    }

    [Fact]
    public void DrawRandomStage_IsOrderIndependent_AndCoversExactlyOnce()
    {
        List<AdvanceChaosCupStageSelection.StageAdvancer> advancers = Enumerable.Range(1, 512)
            .Select(id => new AdvanceChaosCupStageSelection.StageAdvancer(id, $"Athlete {id:0000}"))
            .ToList();
        Pcg32State seed = new(777UL, 888UL);

        IReadOnlyList<IReadOnlyList<AdvanceChaosCupStageSelection.StageAdvancer>> first =
            AdvanceChaosCupStageSelection.DrawRandomStage(advancers, Pcg32V1.Restore(seed), stageIndex: 1);
        List<AdvanceChaosCupStageSelection.StageAdvancer> reversed = advancers.AsEnumerable().Reverse().ToList();
        IReadOnlyList<IReadOnlyList<AdvanceChaosCupStageSelection.StageAdvancer>> second =
            AdvanceChaosCupStageSelection.DrawRandomStage(reversed, Pcg32V1.Restore(seed), stageIndex: 1);

        AssertDrawCoversExactlyOnce(first, second);
    }

    [Fact]
    public async Task Advance_RequiresCompleteStage_AndDrawsExactCounts_WithRetryIdempotence()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-advance-basic-");
        try
        {
            AdvanceChaosCupStageHandler advance = new(store);
            await Should.ThrowAsync<AdvanceChaosCupStageConflictException>(() =>
                advance.HandleAsync(saveId, targetStageIndex: 1, sourceSeasonNumber: 1));
            await CompleteStageAsync(store, saveId, stageIndex: 0);
            AdvanceChaosCupStageResponse drawn = await DrawAndAssertBasicAsync(store, saveId);
            await AssertScorelessTargetAsync(store, saveId, stageIndex: 1);
            await AssertRetryIdempotentAsync(store, saveId, drawn);
            await Should.ThrowAsync<AdvanceChaosCupStageConflictException>(() =>
                advance.HandleAsync(saveId, targetStageIndex: 2, sourceSeasonNumber: 1));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MixedCompletionOrder_YieldsSameNextDraw_WithoutDuplicates()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-advance-order-");
        try
        {
            var (storeA, rootA, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-order-a-");
            var (storeB, rootB, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-order-b-");
            try
            {
                await CompleteStageInOrderAsync(storeA, saveId, stageIndex: 0, Enumerable.Range(1, 32));
                await CompleteStageInOrderAsync(storeB, saveId, stageIndex: 0, Enumerable.Range(1, 32).Reverse());
                await AssertOppositeOrdersMatchAsync(storeA, storeB, saveId);
            }
            finally
            {
                TestSaveStores.DeleteRoot(rootA);
                TestSaveStores.DeleteRoot(rootB);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FullTournament_AdvancesToFinal_WithExactFieldAndExitClassification()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-advance-full-");
        try
        {
            await DrawAllStagesAsync(store, saveId);
            await AssertFinalDrawHoldsAdvancersAsync(store, saveId);
            await CompleteStageAsync(store, saveId, stageIndex: 5);
            await AssertFinalClassificationAsync(store, saveId);
            await AssertTournamentCompleteAsync(store, saveId);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static void AssertDrawCoversExactlyOnce(
        IReadOnlyList<IReadOnlyList<AdvanceChaosCupStageSelection.StageAdvancer>> first,
        IReadOnlyList<IReadOnlyList<AdvanceChaosCupStageSelection.StageAdvancer>> second)
    {
        first.Count.ShouldBe(16);
        foreach (var group in first)
        {
            group.Count.ShouldBe(32);
        }

        List<int> flatFirst = first.SelectMany(g => g.Select(a => a.SaveAthleteId)).ToList();
        List<int> flatSecond = second.SelectMany(g => g.Select(a => a.SaveAthleteId)).ToList();
        flatFirst.ShouldBe(flatSecond);
        flatFirst.Distinct().Count().ShouldBe(512);
    }

    private static async Task<AdvanceChaosCupStageResponse> DrawAndAssertBasicAsync(SaveStore store, Guid saveId)
    {
        AdvanceChaosCupStageHandler advance = new(store);
        (long rngBeforeState, long rngBeforeStream) = await LoadGlobalRngAsync(store, saveId).ConfigureAwait(false);
        AdvanceChaosCupStageResponse drawn =
            await advance.HandleAsync(saveId, targetStageIndex: 1, sourceSeasonNumber: 1).ConfigureAwait(false);
        drawn.PrevStageIndex.ShouldBe(0);
        drawn.StageIndex.ShouldBe(1);
        drawn.GroupCount.ShouldBe(16);
        drawn.EntrantCount.ShouldBe(512);
        drawn.AdvancingFromPrev.ShouldBe(512);
        drawn.IsFinal.ShouldBeFalse();
        drawn.Groups.Count.ShouldBe(16);
        foreach (AdvanceChaosCupStageResponse.DrawGroup group in drawn.Groups)
        {
            group.AthleteIds.Count.ShouldBe(32);
        }

        drawn.Groups.SelectMany(g => g.AthleteIds).Distinct().Count().ShouldBe(512);
        (drawn.RngAfterState == (ulong)rngBeforeState && drawn.RngAfterStream == (ulong)rngBeforeStream).ShouldBeFalse();
        await AssertStageValidAsync(store, saveId, prevStage: 0, nextStage: 1).ConfigureAwait(false);
        return drawn;
    }

    private static async Task AssertScorelessTargetAsync(SaveStore store, Guid saveId, int stageIndex)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        (await context.ChaosCupRounds.CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex).ConfigureAwait(false)).ShouldBe(0);
        (await context.ChaosCupGroupStandings.CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex).ConfigureAwait(false)).ShouldBe(0);
    }

    private static async Task AssertRetryIdempotentAsync(SaveStore store, Guid saveId, AdvanceChaosCupStageResponse drawn)
    {
        AdvanceChaosCupStageHandler advance = new(store);
        (long midState, long midStream) = await LoadGlobalRngAsync(store, saveId).ConfigureAwait(false);
        AdvanceChaosCupStageResponse retry =
            await advance.HandleAsync(saveId, targetStageIndex: 1, sourceSeasonNumber: 1).ConfigureAwait(false);
        retry.DrawChecksum.ShouldBe(drawn.DrawChecksum);
        retry.Groups.SelectMany(g => g.AthleteIds).ShouldBe(drawn.Groups.SelectMany(g => g.AthleteIds).ToList());
        (long afterState, long afterStream) = await LoadGlobalRngAsync(store, saveId).ConfigureAwait(false);
        afterState.ShouldBe(midState);
        afterStream.ShouldBe(midStream);
        AdvanceChaosCupStageResponse queried = await advance.GetAsync(saveId, stageIndex: 1, sourceSeasonNumber: 1).ConfigureAwait(false);
        queried.DrawChecksum.ShouldBe(drawn.DrawChecksum);
    }

    private static async Task AssertOppositeOrdersMatchAsync(SaveStore storeA, SaveStore storeB, Guid saveId)
    {
        AdvanceChaosCupStageResponse nextA =
            await new AdvanceChaosCupStageHandler(storeA).HandleAsync(saveId, targetStageIndex: 1, sourceSeasonNumber: 1).ConfigureAwait(false);
        AdvanceChaosCupStageResponse nextB =
            await new AdvanceChaosCupStageHandler(storeB).HandleAsync(saveId, targetStageIndex: 1, sourceSeasonNumber: 1).ConfigureAwait(false);
        nextA.DrawChecksum.ShouldBe(nextB.DrawChecksum);
        nextA.Groups.SelectMany(g => g.AthleteIds).ShouldBe(nextB.Groups.SelectMany(g => g.AthleteIds).ToList());
        nextA.Groups.SelectMany(g => g.AthleteIds).Distinct().Count().ShouldBe(512);
        await AssertStageValidAsync(storeA, saveId, prevStage: 0, nextStage: 1).ConfigureAwait(false);
        await AssertStageValidAsync(storeB, saveId, prevStage: 0, nextStage: 1).ConfigureAwait(false);
    }

    private static async Task DrawAllStagesAsync(SaveStore store, Guid saveId)
    {
        AdvanceChaosCupStageHandler advance = new(store);
        for (int stage = 0; stage <= 4; stage++)
        {
            await CompleteStageAsync(store, saveId, stageIndex: stage).ConfigureAwait(false);
            AdvanceChaosCupStageResponse drawn =
                await advance.HandleAsync(saveId, targetStageIndex: stage + 1, sourceSeasonNumber: 1).ConfigureAwait(false);
            drawn.StageIndex.ShouldBe(stage + 1);
            drawn.GroupCount.ShouldBe(ChaosCupFormat.GroupCountForStage(stage + 1));
            drawn.EntrantCount.ShouldBe(ChaosCupFormat.EntrantCountForStage(stage + 1));
            drawn.AdvancingFromPrev.ShouldBe(ChaosCupFormat.AdvancingCountForStage(stage));
            drawn.IsFinal.ShouldBe((stage + 1) == 5);
            await AssertStageValidAsync(store, saveId, prevStage: stage, nextStage: stage + 1).ConfigureAwait(false);
        }
    }

    private static async Task AssertFinalDrawHoldsAdvancersAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> stage4 = await context.ChaosCupGroupStandings
            .AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == 4).ToListAsync().ConfigureAwait(false);
        List<ChaosCupDrawEntity> finalDraw = await context.ChaosCupDraws
            .AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == 5).ToListAsync().ConfigureAwait(false);
        finalDraw.Count.ShouldBe(32);
        finalDraw.Select(d => d.GroupNumber).Distinct().ShouldBe([1]);
        ChaosCupInvariants.ValidateAdvancementLinkage(stage4, finalDraw, prevStageIndex: 4);
    }

    private static async Task AssertFinalClassificationAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        var rules = MtgSoloSports.SimulationKernel.Rules.RulesV3.CreateDefault();
        ChaosCupEditionEntity edition = await context.ChaosCupEditions.AsNoTracking().SingleAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false);
        List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        List<ChaosCupRoundEntity> rounds = await context.ChaosCupRounds.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> standings = await context.ChaosCupGroupStandings.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        ValidateAllStages(source, edition, entrants, draws, rounds, standings, rules);
        AssertExitClassification(standings);
    }

    private static async Task AssertTournamentCompleteAsync(SaveStore store, Guid saveId)
    {
        AdvanceChaosCupStageHandler advance = new(store);
        await Should.ThrowAsync<AdvanceChaosCupStageConflictException>(() =>
            advance.HandleAsync(saveId, targetStageIndex: null, sourceSeasonNumber: 1)).ConfigureAwait(false);
        AdvanceChaosCupStageResponse retry =
            await advance.HandleAsync(saveId, targetStageIndex: 5, sourceSeasonNumber: 1).ConfigureAwait(false);
        retry.StageIndex.ShouldBe(5);
        retry.IsFinal.ShouldBeTrue();
        retry.GroupCount.ShouldBe(1);
        retry.EntrantCount.ShouldBe(32);
    }

    private static void ValidateAllStages(
        SeasonEntity source,
        ChaosCupEditionEntity edition,
        List<ChaosCupEntrantEntity> entrants,
        List<ChaosCupDrawEntity> draws,
        List<ChaosCupRoundEntity> rounds,
        List<ChaosCupGroupStandingEntity> standings,
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules)
    {
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, draws.Where(d => d.StageIndex == stage).ToList(), stage);
            ChaosCupInvariants.ValidateStageStandings(
                source, edition,
                draws.Where(d => d.StageIndex == stage).ToList(),
                rounds.Where(r => r.StageIndex == stage).ToList(),
                standings.Where(s => s.StageIndex == stage).ToList(),
                stage, rules);
        }

        for (int stage = 0; stage < 5; stage++)
        {
            ChaosCupInvariants.ValidateAdvancementLinkage(
                standings.Where(s => s.StageIndex == stage).ToList(),
                draws.Where(d => d.StageIndex == stage + 1).ToList(),
                stage);
        }
    }

    private static void AssertExitClassification(List<ChaosCupGroupStandingEntity> standings)
    {
        List<ChaosCupGroupStandingEntity> finalStandings = standings.Where(s => s.StageIndex == 5).ToList();
        finalStandings.Count.ShouldBe(32);
        finalStandings.Select(s => s.GroupRank).OrderBy(r => r).ShouldBe(Enumerable.Range(1, 32).ToList());
        finalStandings.All(s => !s.IsAdvanced).ShouldBeTrue();
        AssertEliminatedCounts(standings, finalStandings);
    }

    private static void AssertEliminatedCounts(
        List<ChaosCupGroupStandingEntity> standings,
        List<ChaosCupGroupStandingEntity> finalStandings)
    {
        standings.Count(s => s.StageIndex == 0 && !s.IsAdvanced).ShouldBe(512);
        standings.Count(s => s.StageIndex == 1 && !s.IsAdvanced).ShouldBe(256);
        standings.Count(s => s.StageIndex == 2 && !s.IsAdvanced).ShouldBe(128);
        standings.Count(s => s.StageIndex == 3 && !s.IsAdvanced).ShouldBe(64);
        standings.Count(s => s.StageIndex == 4 && !s.IsAdvanced).ShouldBe(32);
        HashSet<int> finalists = finalStandings.Select(s => s.SaveAthleteId).ToHashSet();
        finalists.Count.ShouldBe(32);
        HashSet<int> eliminatedNonFinal = standings.Where(s => !s.IsAdvanced && s.StageIndex != 5).Select(s => s.SaveAthleteId).ToHashSet();
        eliminatedNonFinal.Count.ShouldBe(992);
        eliminatedNonFinal.Intersect(finalists).ShouldBeEmpty();
        eliminatedNonFinal.Union(finalists).Count().ShouldBe(1024);
        standings.All(s => s.GroupRank >= 1 && s.GroupRank <= 32).ShouldBeTrue();
        standings.GroupBy(s => new { s.StageIndex, s.SaveAthleteId }).All(g => g.Count() == 1).ShouldBeTrue();
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareFieldAsync(string prefix)
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync(prefix).ConfigureAwait(false);
        await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        return (store, root, saveId);
    }

    private static Task CompleteStageAsync(SaveStore store, Guid saveId, int stageIndex)
    {
        return CompleteStageInOrderAsync(store, saveId, stageIndex, Enumerable.Range(1, ChaosCupFormat.GroupCountForStage(stageIndex)));
    }

    private static async Task CompleteStageInOrderAsync(SaveStore store, Guid saveId, int stageIndex, IEnumerable<int> groupOrder)
    {
        RunChaosCupGroupHandler handler = new(store);
        foreach (int group in groupOrder)
        {
            await handler.FinishGroupAsync(saveId, stageIndex, group, sourceSeasonNumber: 1).ConfigureAwait(false);
        }
    }

    private static async Task AssertStageValidAsync(SaveStore store, Guid saveId, int prevStage, int nextStage)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        var rules = MtgSoloSports.SimulationKernel.Rules.RulesV3.CreateDefault();
        ChaosCupEditionEntity edition = await context.ChaosCupEditions.AsNoTracking().SingleAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false);
        List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
        List<ChaosCupDrawEntity> prevDraws = await context.ChaosCupDraws.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == prevStage).ToListAsync().ConfigureAwait(false);
        List<ChaosCupDrawEntity> nextDraws = await context.ChaosCupDraws.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == nextStage).ToListAsync().ConfigureAwait(false);
        List<ChaosCupRoundEntity> prevRounds = await context.ChaosCupRounds.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == prevStage).ToListAsync().ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> prevStandings = await context.ChaosCupGroupStandings.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == prevStage).ToListAsync().ConfigureAwait(false);
        prevStandings.Count(s => s.IsAdvanced).ShouldBe(ChaosCupFormat.AdvancingCountForStage(prevStage));
        nextDraws.Count.ShouldBe(ChaosCupFormat.EntrantCountForStage(nextStage));
        nextDraws.Select(d => d.SaveAthleteId).Distinct().Count().ShouldBe(nextDraws.Count);
        ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, nextDraws, nextStage);
        ChaosCupInvariants.ValidateAdvancementLinkage(prevStandings, nextDraws, prevStage);
        ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, prevDraws, prevStage);
        ChaosCupInvariants.ValidateStageStandings(source, edition, prevDraws, prevRounds, prevStandings, prevStage, rules);
    }

    private static async Task<(long State, long Stream)> LoadGlobalRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }
}
