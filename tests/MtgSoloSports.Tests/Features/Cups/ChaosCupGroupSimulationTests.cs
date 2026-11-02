using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Cups.RunChaosCupGroup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-101: one 32-athlete Chaos group plays 16 rounds incrementally or fully
/// with isolated group RNG, frozen active bonus and deterministic tie-breakers.
/// Proves incremental next-round calls match instant finish, group order cannot
/// influence other groups, retries are idempotent and league/bonus state is
/// preserved.
/// </summary>
public sealed class ChaosCupGroupSimulationTests
{
    [Fact]
    public async Task Incremental_And_Complete_ProduceIdenticalReplay_AndStandings()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-group-equiv-");
        try
        {
            var (storeA, rootA, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-group-inc-");
            var (storeB, rootB, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-group-full-");
            try
            {
                RunChaosCupGroupHandler handlerA = new(storeA);
                RunChaosCupGroupHandler handlerB = new(storeB);

                RunChaosCupGroupResponse? lastIncremental = null;
                for (int i = 0; i < ChaosCupFormat.RoundsPerGroup; i++)
                {
                    lastIncremental = await handlerA.PlayNextAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
                    lastIncremental.RoundsPlayed.ShouldBe(i + 1);
                    lastIncremental.IsComplete.ShouldBe(i + 1 == ChaosCupFormat.RoundsPerGroup);
                    lastIncremental.Standings.Count.ShouldBe(ChaosCupFormat.GroupSize);
                }

                RunChaosCupGroupResponse finished = await handlerB.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
                finished.RoundsPlayed.ShouldBe(ChaosCupFormat.RoundsPerGroup);
                finished.IsComplete.ShouldBeTrue();

                lastIncremental.ShouldNotBeNull();
                AssertResponsesEqual(lastIncremental!, finished);
                await AssertPersistedGroupsEqualAsync(storeA, storeB, saveId, stageIndex: 0, groupNumber: 1);

                RunChaosCupGroupResponse replayA = await handlerA.GetAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
                RunChaosCupGroupResponse replayB = await handlerB.GetAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
                AssertResponsesEqual(replayA, replayB);
                AssertResponsesEqual(replayA, finished);
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
    public async Task GroupOrder_DoesNotInfluenceOtherGroups_And_LeavesLeagueBonusUntouched()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-group-isolation-");
        try
        {
            var (storeA, rootA, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-group-order-a-");
            var (storeB, rootB, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-group-order-b-");
            try
            {
                (long rngBefore, long streamBefore) = await LoadGlobalRngAsync(storeA, saveId);
                LeagueSnapshot leagueBefore = await CaptureLeagueAsync(storeA, saveId);

                RunChaosCupGroupHandler handlerA = new(storeA);
                RunChaosCupGroupHandler handlerB = new(storeB);

                await handlerA.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
                await handlerA.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 2, sourceSeasonNumber: 1);

                await handlerB.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 2, sourceSeasonNumber: 1);
                await handlerB.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);

                await AssertPersistedGroupsEqualAsync(storeA, storeB, saveId, stageIndex: 0, groupNumber: 1);
                await AssertPersistedGroupsEqualAsync(storeA, storeB, saveId, stageIndex: 0, groupNumber: 2);

                (long rngAfter, long streamAfter) = await LoadGlobalRngAsync(storeA, saveId);
                rngAfter.ShouldBe(rngBefore);
                streamAfter.ShouldBe(streamBefore);

                LeagueSnapshot leagueAfter = await CaptureLeagueAsync(storeA, saveId);
                leagueAfter.ShouldBe(leagueBefore);
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
    public async Task Retry_FinishIsIdempotent_PlayNextAfterCompleteConflicts()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-group-retry-");
        try
        {
            RunChaosCupGroupHandler handler = new(store);
            RunChaosCupGroupResponse first = await handler.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
            first.IsComplete.ShouldBeTrue();

            RunChaosCupGroupResponse retry = await handler.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
            AssertResponsesEqual(first, retry);

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
                (await context.ChaosCupRounds.CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == 0 && e.GroupNumber == 1)).ShouldBe(ChaosCupFormat.RoundsPerGroup);
                (await context.ChaosCupGroupStandings.CountAsync(e => e.SourceSeasonId == source.Id && e.StageIndex == 0 && e.GroupNumber == 1)).ShouldBe(ChaosCupFormat.GroupSize);
            }

            await Should.ThrowAsync<RunChaosCupGroupConflictException>(() =>
                handler.PlayNextAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1));

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
                ChaosCupEditionEntity edition = await context.ChaosCupEditions.AsNoTracking().SingleAsync(e => e.SourceSeasonId == source.Id);
                var rules = MtgSoloSports.SimulationKernel.Rules.RulesV3.CreateDefault();
                List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync();
                List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == 0).ToListAsync();
                List<ChaosCupRoundEntity> rounds = await context.ChaosCupRounds.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == 0 && e.GroupNumber == 1).OrderBy(e => e.RoundNumber).ToListAsync();
                List<ChaosCupGroupStandingEntity> standings = await context.ChaosCupGroupStandings.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == 0).ToListAsync();
                ChaosCupInvariants.ValidateGroupRoundsContiguous(rounds, 0, 1);
                foreach (ChaosCupRoundEntity row in rounds)
                {
                    ChaosCupInvariants.ValidatePersistedRound(row, source, edition, rules);
                }

                ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, draws, 0);
                ChaosCupInvariants.ValidateStageStandings(source, edition, draws, await context.ChaosCupRounds.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == 0).ToListAsync(), standings, 0, rules);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task GroupRng_IsIsolated_Derived_And_DistinctPerGroup()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-group-rng-");
        try
        {
            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            ChaosCupEditionEntity edition = await context.ChaosCupEditions.AsNoTracking().SingleAsync(e => e.SourceSeasonId == source.Id);
            List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws.AsNoTracking().Where(e => e.SourceSeasonId == source.Id && e.StageIndex == 0).ToListAsync();

            var firstA = ChaosCupGroupSimulation.DeriveGroupRng(edition, draws, stageIndex: 0, groupNumber: 1);
            var firstB = ChaosCupGroupSimulation.DeriveGroupRng(edition, draws, stageIndex: 0, groupNumber: 1);
            firstA.ShouldBe(firstB);

            var second = ChaosCupGroupSimulation.DeriveGroupRng(edition, draws, stageIndex: 0, groupNumber: 2);
            second.ShouldNotBe(firstA);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareFieldAsync(string prefix)
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync(prefix).ConfigureAwait(false);
        await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        return (store, root, saveId);
    }

    private static void AssertResponsesEqual(RunChaosCupGroupResponse left, RunChaosCupGroupResponse right)
    {
        left.RoundsPlayed.ShouldBe(right.RoundsPlayed);
        left.IsComplete.ShouldBe(right.IsComplete);
        left.Rounds.Count.ShouldBe(right.Rounds.Count);
        for (int i = 0; i < left.Rounds.Count; i++)
        {
            left.Rounds[i].ShouldBe(right.Rounds[i]);
        }

        left.Standings.Count.ShouldBe(right.Standings.Count);
        for (int i = 0; i < left.Standings.Count; i++)
        {
            left.Standings[i].ShouldBe(right.Standings[i]);
        }
    }

    private static async Task AssertPersistedGroupsEqualAsync(SaveStore storeA, SaveStore storeB, Guid saveId, int stageIndex, int groupNumber)
    {
        List<ChaosCupRoundPayloadDocument> roundsA = await LoadGroupPayloadsAsync(storeA, saveId, stageIndex, groupNumber).ConfigureAwait(false);
        List<ChaosCupRoundPayloadDocument> roundsB = await LoadGroupPayloadsAsync(storeB, saveId, stageIndex, groupNumber).ConfigureAwait(false);
        roundsA.Count.ShouldBe(ChaosCupFormat.RoundsPerGroup);
        roundsB.Count.ShouldBe(ChaosCupFormat.RoundsPerGroup);
        for (int i = 0; i < roundsA.Count; i++)
        {
            AssertPayloadsEqual(roundsA[i], roundsB[i]);
        }

        List<ChaosCupGroupStandingEntity> standingsA = await LoadGroupStandingsAsync(storeA, saveId, stageIndex, groupNumber).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> standingsB = await LoadGroupStandingsAsync(storeB, saveId, stageIndex, groupNumber).ConfigureAwait(false);
        standingsA.Count.ShouldBe(ChaosCupFormat.GroupSize);
        standingsB.Count.ShouldBe(ChaosCupFormat.GroupSize);
        foreach (var (a, b) in standingsA.OrderBy(s => s.GroupRank).Zip(standingsB.OrderBy(s => s.GroupRank)))
        {
            a.SaveAthleteId.ShouldBe(b.SaveAthleteId);
            a.GroupRank.ShouldBe(b.GroupRank);
            a.GroupScoreThousandths.ShouldBe(b.GroupScoreThousandths);
            a.BaseScoreThousandths.ShouldBe(b.BaseScoreThousandths);
            a.RoundWins.ShouldBe(b.RoundWins);
            a.RoundPlaceCountsJson.ShouldBe(b.RoundPlaceCountsJson);
            a.IsAdvanced.ShouldBe(b.IsAdvanced);
        }
    }

    private static void AssertPayloadsEqual(ChaosCupRoundPayloadDocument left, ChaosCupRoundPayloadDocument right)
    {
        left.StageIndex.ShouldBe(right.StageIndex);
        left.GroupNumber.ShouldBe(right.GroupNumber);
        left.RoundNumber.ShouldBe(right.RoundNumber);
        left.RngBeforeState.ShouldBe(right.RngBeforeState);
        left.RngBeforeStream.ShouldBe(right.RngBeforeStream);
        left.RngAfterState.ShouldBe(right.RngAfterState);
        left.RngAfterStream.ShouldBe(right.RngAfterStream);
        left.Checksum.ShouldBe(right.Checksum);
        left.Placements.Count.ShouldBe(right.Placements.Count);
        for (int i = 0; i < left.Placements.Count; i++)
        {
            left.Placements[i].ShouldBe(right.Placements[i]);
        }
    }

    private static async Task<List<ChaosCupRoundPayloadDocument>> LoadGroupPayloadsAsync(SaveStore store, Guid saveId, int stageIndex, int groupNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<ChaosCupRoundEntity> rows = await context.ChaosCupRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.GroupNumber == groupNumber)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync().ConfigureAwait(false);
        return rows.Select(r => ChaosCupRoundPayloadDocument.FromStored(r.PayloadJson)).ToList();
    }

    private static async Task<List<ChaosCupGroupStandingEntity>> LoadGroupStandingsAsync(SaveStore store, Guid saveId, int stageIndex, int groupNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        return await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.GroupNumber == groupNumber)
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task<(long State, long Stream)> LoadGlobalRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }

    private sealed record LeagueSnapshot(
        int StageStandings,
        int SeasonStandings,
        int LeagueRounds,
        long Lifetime,
        long Effective,
        long Championship);

    private static async Task<LeagueSnapshot> CaptureLeagueAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int stages = await context.StageStandings.CountAsync().ConfigureAwait(false);
        int seasons = await context.SeasonStandings.CountAsync().ConfigureAwait(false);
        int rounds = await context.Rounds.CountAsync().ConfigureAwait(false);
        long lifetime = await context.AthleteCareers.SumAsync(e => (long)e.LifetimeEarnedBonusThousandths).ConfigureAwait(false);
        long effective = await context.AthleteCareers.SumAsync(e => (long)e.CurrentEffectiveBonusThousandths).ConfigureAwait(false);
        long championship = await context.SeasonStandings.SumAsync(e => (long)e.TotalChampionshipPointsThousandths).ConfigureAwait(false);
        return new LeagueSnapshot(stages, seasons, rounds, lifetime, effective, championship);
    }
}
