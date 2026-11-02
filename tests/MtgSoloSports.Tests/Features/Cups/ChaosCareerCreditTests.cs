using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CompleteChaosCup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.GetHallOfFame;
using MtgSoloSports.Features.Records.ListHonours;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Shared.Careers;
using MtgSoloSports.SimulationKernel.Careers;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-104: Chaos medals and 450-weight career credit. The completed 1024-entrant
/// tournament yields exactly one positive career event per entrant (Pool included,
/// counted once) and exactly three honours (Final P1..P3). Scores follow stage
/// order with Final P32 strictly above the semifinal exit; old saves without
/// Chaos contribute zero Chaos points.
/// </summary>
public sealed class ChaosCareerCreditTests
{
    [Fact]
    public void ChaosScoringCurve_Champion675_StageOrdered_FinalAboveExit()
    {
        int champion = Score(CareerPerformanceScorer.Category.ChaosCupIndividual, 1, 32, true);
        champion.ShouldBe(675);

        int finalSecond = Score(CareerPerformanceScorer.Category.ChaosCupIndividual, 2, 32, true);
        int finalThird = Score(CareerPerformanceScorer.Category.ChaosCupIndividual, 3, 32, true);
        int finalLast = Score(CareerPerformanceScorer.Category.ChaosCupIndividual, 32, 32, true);
        champion.ShouldBeGreaterThan(finalSecond);
        finalSecond.ShouldBeGreaterThan(finalThird);
        finalThird.ShouldBeGreaterThan(finalLast);
        finalLast.ShouldBe(13);

        CareerPerformanceScorer.ChaosExitPointsForRank(1).ShouldBe(10);
        CareerPerformanceScorer.ChaosExitPointsForRank(5).ShouldBe(1);
        finalLast.ShouldBeGreaterThan(CareerPerformanceScorer.ChaosExitPointsForRank(1));
        CareerPerformanceScorer.ChaosCupIndividualWeight.ShouldBe(450);
        CareerPerformanceScorer.WeightsVersion.ShouldBe("MSS-104-v1");

        Should.Throw<InvalidOperationException>(() => CareerPerformanceScorer.ChaosExitPointsForRank(0));
        Should.Throw<InvalidOperationException>(() =>
            CareerPerformanceScorer.ScoreEvent(new CareerPerformanceScorer.PlacementEvent(1, 1, CareerPerformanceScorer.Category.ChaosCupIndividual, 1, 32, false)));
    }

    [Fact]
    public void HonourMapper_ChaosPodium_MapsOnlyFinalTopThree()
    {
        HonourKindMapper.FromChaosCupIndividualRank(1).ShouldBe(HonourKind.ChaosCupIndividualChampion);
        HonourKindMapper.FromChaosCupIndividualRank(2).ShouldBe(HonourKind.ChaosCupIndividualRunnerUp);
        HonourKindMapper.FromChaosCupIndividualRank(3).ShouldBe(HonourKind.ChaosCupIndividualThirdPlace);
        Should.Throw<ArgumentOutOfRangeException>(() => HonourKindMapper.FromChaosCupIndividualRank(4));
        HonourKindMapper.IsChampionKind((int)HonourKind.ChaosCupIndividualChampion).ShouldBeTrue();
        HonourKindMapper.IsChampionKind((int)HonourKind.ChaosCupIndividualRunnerUp).ShouldBeFalse();
    }

    [Fact]
    public async Task CompletedTournament_1024CreditsAndThreeMedals_Idempotent()
    {
        // Incidental Season 1 history is forked; the Chaos field plus full-tournament
        // completion below is the named action under test.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-credit-");
        try
        {
            await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            CompleteChaosCupResponse cupped = await new CompleteChaosCupHandler(store).CompleteCupAsync(saveId, sourceSeasonNumber: 1);
            cupped.IsComplete.ShouldBeTrue();

            await AssertTournamentCreditsAsync(store, saveId);
            await AssertRetryIdempotentAsync(store, saveId);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task IncompleteChaos_ContributesNothing_OldSavesStayClean()
    {
        // Incidental Season 1 history is forked; no Chaos edition exists, so the
        // loader must contribute zero Chaos points and old totals stay intact.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-compat-");
        try
        {
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                CareerPerformanceLoader.LoadResult loaded = await CareerPerformanceLoader.LoadAsync(
                    context, null, null, CancellationToken.None);
                loaded.Events.Any(e => e.EventCategory == CareerPerformanceScorer.Category.ChaosCupIndividual).ShouldBeFalse();
            }

            await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                CareerPerformanceLoader.LoadResult pending = await CareerPerformanceLoader.LoadAsync(
                    context, 1, 1, CancellationToken.None);
                pending.Events.Any(e => e.EventCategory == CareerPerformanceScorer.Category.ChaosCupIndividual).ShouldBeFalse();
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task AssertTournamentCreditsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        CareerPerformanceLoader.LoadResult loaded = await CareerPerformanceLoader.LoadAsync(
            context, 1, 1, CancellationToken.None).ConfigureAwait(false);
        List<CareerPerformanceScorer.PlacementEvent> chaos = loaded.Events
            .Where(e => e.EventCategory == CareerPerformanceScorer.Category.ChaosCupIndividual)
            .ToList();
        chaos.Count.ShouldBe(1024);
        chaos.Select(e => e.AthleteId).Distinct().Count().ShouldBe(1024);

        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> scores =
            CareerPerformanceScorer.ComputeScores(loaded.Events);
        foreach (CareerPerformanceScorer.PlacementEvent scored in chaos)
        {
            scores[scored.AthleteId].ChaosCupIndividualPoints.ShouldBeGreaterThan(0);
        }

        await AssertStageOrderAsync(context, chaos).ConfigureAwait(false);
        await AssertPoolAndHonoursAsync(store, saveId, context, loaded, chaos, scores).ConfigureAwait(false);
    }

    private static async Task AssertStageOrderAsync(
        SaveDbContext context,
        List<CareerPerformanceScorer.PlacementEvent> chaos)
    {
        Dictionary<int, int> pointsByAthlete = chaos.ToDictionary(
            e => e.AthleteId, e => CareerPerformanceScorer.ScoreEvent(e));
        List<ChaosCupGroupStandingEntity> finals = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonNumber == 1 && e.StageIndex == 5)
            .OrderBy(e => e.GroupRank)
            .ToListAsync().ConfigureAwait(false);
        finals.Count.ShouldBe(32);
        for (int i = 1; i < finals.Count; i++)
        {
            pointsByAthlete[finals[i - 1].SaveAthleteId]
                .ShouldBeGreaterThan(pointsByAthlete[finals[i].SaveAthleteId]);
        }

        int bestExit = chaos.Where(e => !e.IsFinal && e.Rank == 1).Select(CareerPerformanceScorer.ScoreEvent).First();
        pointsByAthlete[finals[^1].SaveAthleteId].ShouldBeGreaterThan(bestExit);

        List<IGrouping<int, CareerPerformanceScorer.PlacementEvent>> exits = chaos
            .Where(e => !e.IsFinal).GroupBy(e => e.Rank).OrderBy(g => g.Key).ToList();
        exits.Count.ShouldBe(5);
        foreach (IGrouping<int, CareerPerformanceScorer.PlacementEvent> group in exits)
        {
            group.Select(CareerPerformanceScorer.ScoreEvent).Distinct().Count().ShouldBe(1);
        }
    }

    private static async Task AssertPoolAndHonoursAsync(
        SaveStore store,
        Guid saveId,
        SaveDbContext context,
        CareerPerformanceLoader.LoadResult loaded,
        List<CareerPerformanceScorer.PlacementEvent> chaos,
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> scores)
    {
        List<int> poolIds = await context.ChaosCupEntrants
            .AsNoTracking()
            .Where(e => e.SourceSeasonNumber == 1 && e.IsPoolInvite)
            .Select(e => e.SaveAthleteId)
            .ToListAsync().ConfigureAwait(false);
        poolIds.Count.ShouldBeGreaterThan(0);
        foreach (int poolId in poolIds.Take(5))
        {
            chaos.Any(e => e.AthleteId == poolId).ShouldBeTrue();
        }

        List<ChaosCupGroupStandingEntity> finals = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonNumber == 1 && e.StageIndex == 5)
            .OrderBy(e => e.GroupRank)
            .ToListAsync().ConfigureAwait(false);
        List<HonourEntity> honours = await context.Honours
            .AsNoTracking()
            .Where(e => e.SeasonNumber == 1 && (e.Kind == (int)HonourKind.ChaosCupIndividualChampion
                || e.Kind == (int)HonourKind.ChaosCupIndividualRunnerUp
                || e.Kind == (int)HonourKind.ChaosCupIndividualThirdPlace))
            .ToListAsync().ConfigureAwait(false);
        honours.Count.ShouldBe(3);
        honours.Single(h => h.Kind == (int)HonourKind.ChaosCupIndividualChampion).SaveAthleteId
            .ShouldBe(finals[0].SaveAthleteId);

        ListHonoursHandler listHandler = new(store);
        ListHonoursResponse listed = await listHandler.HandleAsync(saveId).ConfigureAwait(false);
        CountHonour(listed, nameof(HonourKind.ChaosCupIndividualChampion)).ShouldBe(1);
        CountHonour(listed, nameof(HonourKind.ChaosCupIndividualRunnerUp)).ShouldBe(1);
        CountHonour(listed, nameof(HonourKind.ChaosCupIndividualThirdPlace)).ShouldBe(1);

        GetHallOfFameHandler fameHandler = new(store);
        GetHallOfFameResponse fame = await fameHandler.HandleAsync(saveId, 100, 1, 1).ConfigureAwait(false);
        fame.WeightsVersion.ShouldBe("MSS-104-v1");
        fame.Leaders.ShouldNotBeEmpty();

        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> viaSelection =
            await CareerPerformanceLoader.LoadScoresUpToAsync(context, 1, athleteIds: null, CancellationToken.None).ConfigureAwait(false);
        viaSelection.Count.ShouldBe(scores.Count);
    }

    private static async Task AssertRetryIdempotentAsync(SaveStore store, Guid saveId)
    {
        CompleteChaosCupResponse retry = await new CompleteChaosCupHandler(store).CompleteCupAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        retry.IsComplete.ShouldBeTrue();
        retry.RoundsPlayedThisCall.ShouldBe(0);
        using SaveDbContext context = store.OpenDbContext(saveId);
        int honours = await context.Honours.CountAsync(e => e.SeasonNumber == 1
            && (e.Kind == (int)HonourKind.ChaosCupIndividualChampion
                || e.Kind == (int)HonourKind.ChaosCupIndividualRunnerUp
                || e.Kind == (int)HonourKind.ChaosCupIndividualThirdPlace)).ConfigureAwait(false);
        honours.ShouldBe(3);
        CareerPerformanceLoader.LoadResult reloaded = await CareerPerformanceLoader.LoadAsync(
            context, 1, 1, CancellationToken.None).ConfigureAwait(false);
        reloaded.Events.Count(e => e.EventCategory == CareerPerformanceScorer.Category.ChaosCupIndividual).ShouldBe(1024);
    }

    private static int CountHonour(ListHonoursResponse listed, string kind) =>
        listed.Honours.Count(h => string.Equals(h.HonourKind, kind, StringComparison.Ordinal));

    private static int Score(CareerPerformanceScorer.Category category, int rank, int fieldSize, bool isFinal) =>
        CareerPerformanceScorer.ScoreEvent(new CareerPerformanceScorer.PlacementEvent(7, 3, category, rank, fieldSize, isFinal));
}
