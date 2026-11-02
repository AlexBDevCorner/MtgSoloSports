using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.AdvanceChaosCupStage;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Cups.CompleteChaosCup;
using MtgSoloSports.Features.Cups.RunChaosCupGroup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-103: bulk Chaos Cup stage and tournament completion reuses the one-group
/// complete action and persisted draws. Proves complete-stage matches
/// incremental groups, full-Cup matches manual stage/draw progression across
/// group orders, partial groups resume, and retries consume no duplicate
/// rounds, bonus or RNG.
/// </summary>
public sealed class CompleteChaosCupTests
{
    [Fact]
    public async Task CompleteStage_MatchesIncrementalGroups_AndReportsProgress()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-bulk-stage-");
        try
        {
            var (storeA, rootA, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-stage-manual-");
            var (storeB, rootB, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-stage-bulk-");
            try
            {
                RunChaosCupGroupHandler manual = new(storeA);
                for (int group = 1; group <= ChaosCupFormat.GroupCountForStage(0); group++)
                {
                    await manual.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: group, sourceSeasonNumber: 1);
                }

                CompleteChaosCupHandler bulk = new(storeB);
                CompleteChaosCupStageResponse staged = await bulk.CompleteStageAsync(saveId, stageIndex: 0, sourceSeasonNumber: 1);
                staged.StageIndex.ShouldBe(0);
                staged.GroupCount.ShouldBe(ChaosCupFormat.GroupCountForStage(0));
                staged.EntrantCount.ShouldBe(ChaosCupFormat.EntrantCountForStage(0));
                staged.GroupsCompleted.ShouldBe(ChaosCupFormat.GroupCountForStage(0));
                staged.TotalRounds.ShouldBe(ChaosCupFormat.GroupCountForStage(0) * ChaosCupFormat.RoundsPerGroup);
                staged.RoundsPlayedTotal.ShouldBe(staged.TotalRounds);
                staged.RoundsPlayedThisCall.ShouldBe(staged.TotalRounds);
                staged.IsComplete.ShouldBeTrue();

                await AssertStagesEqualAsync(storeA, storeB, saveId, stageIndex: 0);

                CompleteChaosCupStageResponse retry = await bulk.CompleteStageAsync(saveId, stageIndex: 0, sourceSeasonNumber: 1);
                retry.IsComplete.ShouldBeTrue();
                retry.RoundsPlayedThisCall.ShouldBe(0);
                retry.RoundsPlayedTotal.ShouldBe(staged.RoundsPlayedTotal);
                await AssertStagesEqualAsync(storeA, storeB, saveId, stageIndex: 0);
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
    public async Task CompleteStage_ResumesPartialGroups_AndRejectsOutOfOrder()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-bulk-resume-");
        try
        {
            var (storeA, rootA, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-resume-manual-");
            var (storeB, rootB, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-resume-bulk-");
            try
            {
                RunChaosCupGroupHandler partial = new(storeB);
                for (int i = 0; i < 5; i++)
                {
                    await partial.PlayNextAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
                }

                for (int i = 0; i < 3; i++)
                {
                    await partial.PlayNextAsync(saveId, stageIndex: 0, groupNumber: 2, sourceSeasonNumber: 1);
                }

                CompleteChaosCupHandler bulk = new(storeB);
                CompleteChaosCupStageResponse staged = await bulk.CompleteStageAsync(saveId, stageIndex: 0, sourceSeasonNumber: 1);
                staged.IsComplete.ShouldBeTrue();
                staged.RoundsPlayedTotal.ShouldBe(ChaosCupFormat.GroupCountForStage(0) * ChaosCupFormat.RoundsPerGroup);
                staged.RoundsPlayedThisCall.ShouldBe(staged.TotalRounds - 8);

                RunChaosCupGroupHandler manual = new(storeA);
                for (int group = 1; group <= ChaosCupFormat.GroupCountForStage(0); group++)
                {
                    await manual.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: group, sourceSeasonNumber: 1);
                }

                await AssertStagesEqualAsync(storeA, storeB, saveId, stageIndex: 0);

                CompleteChaosCupHandler bulkA = new(storeA);
                await Should.ThrowAsync<CompleteChaosCupConflictException>(() =>
                    bulkA.CompleteStageAsync(saveId, stageIndex: 1, sourceSeasonNumber: 1));
                await Should.ThrowAsync<ArgumentException>(() =>
                    bulkA.CompleteStageAsync(saveId, stageIndex: 9, sourceSeasonNumber: 1));
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
    public async Task CompleteCup_MatchesManualTournament_WithReverseGroupOrder()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-bulk-cup-");
        try
        {
            var (storeA, rootA, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-cup-manual-");
            var (storeB, rootB, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-cup-bulk-");
            try
            {
                await CompleteManuallyInReverseAsync(storeA, saveId);
                CompleteChaosCupResponse cupped = await new CompleteChaosCupHandler(storeB).CompleteCupAsync(saveId, sourceSeasonNumber: 1);
                cupped.StagesTotal.ShouldBe(ChaosCupFormat.StageCount);
                cupped.StagesCompleted.ShouldBe(ChaosCupFormat.StageCount);
                cupped.TotalGroups.ShouldBe(ChaosCupFormat.StageGroupCounts.Sum());
                cupped.TotalRounds.ShouldBe(cupped.TotalGroups * ChaosCupFormat.RoundsPerGroup);
                cupped.RoundsPlayedTotal.ShouldBe(cupped.TotalRounds);
                cupped.RoundsPlayedThisCall.ShouldBe(cupped.TotalRounds);
                cupped.IsComplete.ShouldBeTrue();

                await AssertTournamentsEqualAsync(storeA, storeB, saveId);

                (long stateBefore, long streamBefore) = await LoadGlobalRngAsync(storeB, saveId);
                CompleteChaosCupResponse retry = await new CompleteChaosCupHandler(storeB).CompleteCupAsync(saveId, sourceSeasonNumber: 1);
                retry.IsComplete.ShouldBeTrue();
                retry.RoundsPlayedThisCall.ShouldBe(0);
                (long stateAfter, long streamAfter) = await LoadGlobalRngAsync(storeB, saveId);
                stateAfter.ShouldBe(stateBefore);
                streamAfter.ShouldBe(streamBefore);
                await AssertTournamentsEqualAsync(storeA, storeB, saveId);
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

    private static async Task CompleteManuallyInReverseAsync(SaveStore store, Guid saveId)
    {
        RunChaosCupGroupHandler groups = new(store);
        AdvanceChaosCupStageHandler draws = new(store);
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            foreach (int group in Enumerable.Range(1, ChaosCupFormat.GroupCountForStage(stage)).Reverse())
            {
                await groups.FinishGroupAsync(saveId, stage, group, sourceSeasonNumber: 1).ConfigureAwait(false);
            }

            if (stage < ChaosCupFormat.StageCount - 1)
            {
                await draws.HandleAsync(saveId, targetStageIndex: stage + 1, sourceSeasonNumber: 1).ConfigureAwait(false);
            }
        }
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareFieldAsync(string prefix)
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync(prefix).ConfigureAwait(false);
        await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        return (store, root, saveId);
    }

    private static async Task AssertStagesEqualAsync(SaveStore left, SaveStore right, Guid saveId, int stageIndex)
    {
        List<ChaosCupRoundPayloadDocument> leftRounds = await LoadStagePayloadsAsync(left, saveId, stageIndex).ConfigureAwait(false);
        List<ChaosCupRoundPayloadDocument> rightRounds = await LoadStagePayloadsAsync(right, saveId, stageIndex).ConfigureAwait(false);
        leftRounds.Count.ShouldBe(rightRounds.Count);
        for (int i = 0; i < leftRounds.Count; i++)
        {
            AssertPayloadsEqual(leftRounds[i], rightRounds[i]);
        }

        List<ChaosCupGroupStandingEntity> leftStandings = await LoadStageStandingsAsync(left, saveId, stageIndex).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> rightStandings = await LoadStageStandingsAsync(right, saveId, stageIndex).ConfigureAwait(false);
        AssertStandingsEqual(leftStandings, rightStandings);
    }

    private static async Task AssertTournamentsEqualAsync(SaveStore left, SaveStore right, Guid saveId)
    {
        for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
        {
            string leftDraw = await LoadStageDrawChecksumAsync(left, saveId, stage).ConfigureAwait(false);
            string rightDraw = await LoadStageDrawChecksumAsync(right, saveId, stage).ConfigureAwait(false);
            leftDraw.ShouldBe(rightDraw);
            await AssertStagesEqualAsync(left, right, saveId, stage).ConfigureAwait(false);
        }

        using SaveDbContext leftContext = left.OpenDbContext(saveId);
        using SaveDbContext rightContext = right.OpenDbContext(saveId);
        SeasonEntity source = await leftContext.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        var rules = MtgSoloSports.SimulationKernel.Rules.RulesV3.CreateDefault();
        ChaosCupEditionEntity edition = await leftContext.ChaosCupEditions.AsNoTracking().SingleAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false);
        foreach (SaveStore store in new[] { left, right })
        {
            using SaveDbContext context = store.OpenDbContext(saveId);
            List<ChaosCupEntrantEntity> entrants = await context.ChaosCupEntrants.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
            List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
            List<ChaosCupRoundEntity> rounds = await context.ChaosCupRounds.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
            List<ChaosCupGroupStandingEntity> standings = await context.ChaosCupGroupStandings.AsNoTracking().Where(e => e.SourceSeasonId == source.Id).ToListAsync().ConfigureAwait(false);
            for (int stage = 0; stage < ChaosCupFormat.StageCount; stage++)
            {
                ChaosCupInvariants.ValidateStageDraw(source, edition, entrants, draws.Where(d => d.StageIndex == stage).ToList(), stage);
                ChaosCupInvariants.ValidateStageStandings(source, edition, draws.Where(d => d.StageIndex == stage).ToList(), rounds.Where(r => r.StageIndex == stage).ToList(), standings.Where(s => s.StageIndex == stage).ToList(), stage, rules);
            }
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

    private static void AssertStandingsEqual(List<ChaosCupGroupStandingEntity> left, List<ChaosCupGroupStandingEntity> right)
    {
        left.Count.ShouldBe(right.Count);
        foreach (var (a, b) in left.OrderBy(s => s.StageIndex).ThenBy(s => s.GroupNumber).ThenBy(s => s.GroupRank)
                     .Zip(right.OrderBy(s => s.StageIndex).ThenBy(s => s.GroupNumber).ThenBy(s => s.GroupRank)))
        {
            a.StageIndex.ShouldBe(b.StageIndex);
            a.GroupNumber.ShouldBe(b.GroupNumber);
            a.SaveAthleteId.ShouldBe(b.SaveAthleteId);
            a.GroupRank.ShouldBe(b.GroupRank);
            a.GroupScoreThousandths.ShouldBe(b.GroupScoreThousandths);
            a.BaseScoreThousandths.ShouldBe(b.BaseScoreThousandths);
            a.RoundWins.ShouldBe(b.RoundWins);
            a.RoundPlaceCountsJson.ShouldBe(b.RoundPlaceCountsJson);
            a.IsAdvanced.ShouldBe(b.IsAdvanced);
        }
    }

    private static async Task<List<ChaosCupRoundPayloadDocument>> LoadStagePayloadsAsync(SaveStore store, Guid saveId, int stageIndex)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<ChaosCupRoundEntity> rows = await context.ChaosCupRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
            .OrderBy(e => e.GroupNumber)
            .ThenBy(e => e.RoundNumber)
            .ToListAsync().ConfigureAwait(false);
        return rows.Select(r => ChaosCupRoundPayloadDocument.FromStored(r.PayloadJson)).ToList();
    }

    private static async Task<List<ChaosCupGroupStandingEntity>> LoadStageStandingsAsync(SaveStore store, Guid saveId, int stageIndex)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        return await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
            .ToListAsync().ConfigureAwait(false);
    }

    private static async Task<string> LoadStageDrawChecksumAsync(SaveStore store, Guid saveId, int stageIndex)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<ChaosCupDrawEntity> draws = await context.ChaosCupDraws
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex)
            .ToListAsync().ConfigureAwait(false);
        draws.Count.ShouldBe(ChaosCupFormat.EntrantCountForStage(stageIndex));
        return draws[0].DrawChecksum;
    }

    private static async Task<(long State, long Stream)> LoadGlobalRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }
}
