using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Features.Qualifiers.GetQualifierRounds;
using MtgSoloSports.Features.Qualifiers.PlayFeederQualifierRound;
using MtgSoloSports.Features.Superleague.PlayQualifierRound;
using MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Qualifiers;

/// <summary>
/// MSS-069 acceptance: every postseason qualifier is a real Live event with
/// its own 16-round playthrough, cumulative standings and historical replay.
/// Test methods never use ConfigureAwait (xUnit1030); helpers always do.
/// </summary>
public sealed class FeederQualifierLiveTests
{
    [Fact]
    public async Task FeederLive_PendingOpensBeforeRunning_PlaysOneRoundAtATime()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierPreResolveAsync("mtgsolosports-qual69-");
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);
            await new RunQualifierHandler(store).HandleAsync(saveId);

            var reads = new GetQualifierRoundsHandler(store);
            GetQualifierRoundsResponse pending = await reads.HandleListAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White, fromSeasonNumber: 2);
            pending.RoundsPlayed.ShouldBe(0);
            pending.TotalRounds.ShouldBe(16);
            pending.IsComplete.ShouldBeFalse();
            pending.Field.Count.ShouldBe(16);

            var step = new PlayFeederQualifierRoundHandler(store);
            PlayFeederQualifierRoundResponse first = await step.HandleAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White);
            first.RoundsPlayed.ShouldBe(1);
            first.TotalRounds.ShouldBe(16);
            first.IsComplete.ShouldBeFalse();

            GetQualifierRoundsResponse afterOne = await reads.HandleListAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White, fromSeasonNumber: 2);
            afterOne.RoundsPlayed.ShouldBe(1);

            var round = await reads.HandleRoundAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White, 1, fromSeasonNumber: 2);
            round.RoundNumber.ShouldBe(1);

            for (int expected = 2; expected <= 15; expected++)
            {
                PlayFeederQualifierRoundResponse played = await step.HandleAsync(
                    saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White);
                played.RoundsPlayed.ShouldBe(expected);
            }

            PlayFeederQualifierRoundResponse last = await step.HandleAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White);
            last.RoundsPlayed.ShouldBe(16);
            last.IsComplete.ShouldBeTrue();

            GetQualifierRoundsResponse done = await reads.HandleListAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White, fromSeasonNumber: 2);
            done.IsComplete.ShouldBeTrue();
            done.Standings.Count(s => s.IsQualified).ShouldBe(8);

            (await CountRoundsAsync(store, saveId)).ShouldBe(16 + 16);
            await Should.ThrowAsync<RunFeederQualifierConflictException>(() => step.HandleAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White));
            (await CountRoundsAsync(store, saveId)).ShouldBe(16 + 16);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FeederLive_RejectsOutOfOrderAndWrongIdentity()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierPreResolveAsync("mtgsolosports-qual69-");
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);
            await new RunQualifierHandler(store).HandleAsync(saveId);

            var step = new PlayFeederQualifierRoundHandler(store);
            var single = new BoundaryQualifierRunner(store);

            await Should.ThrowAsync<RunFeederQualifierConflictException>(() => step.HandleAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.Blue));
            await Should.ThrowAsync<RunFeederQualifierConflictException>(() => single.HandleAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.Blue));
            await Should.ThrowAsync<RunFeederQualifierConflictException>(() => step.HandleAsync(
                saveId, QualifierBoundary.Feeder2Feeder3, (int)SportingColor.White));

            (await CountRoundsAsync(store, saveId)).ShouldBe(16);

            await step.HandleAsync(saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White);
            await Should.ThrowAsync<RunFeederQualifierConflictException>(() => step.HandleAsync(
                saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.Black));
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FeederLive_PartialResumeAndRunAllGiveIdenticalOutcomes()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierPreResolveAsync("mtgsolosports-qual69a-");
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);
            var (secondStore, secondRoot, secondId) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-qual69b-");
            var (thirdStore, thirdRoot, thirdId) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-qual69c-");
            try
            {
                await PlayAllPerRoundAsync(store, saveId);
                Snapshot a = await CaptureAsync(store, saveId);

                await new RunAllQualifiersHandler(secondStore).HandleAsync(secondId);
                Snapshot b = await CaptureAsync(secondStore, secondId);

                await PlayMixedThenRunAllAsync(thirdStore, thirdId);
                Snapshot c = await CaptureAsync(thirdStore, thirdId);

                AssertSnapshotsEqual(a, b);
                AssertSnapshotsEqual(a, c);
            }
            finally
            {
                TestSaveStores.DeleteRoot(secondRoot);
                TestSaveStores.DeleteRoot(thirdRoot);
            }
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FeederLive_ReplayAfterLaterSeasonIsReadOnly()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkQualifierPreResolveAsync("mtgsolosports-qual69-");
        try
        {
            await new ResolveAutomaticMovementHandler(store).HandleAsync(saveId);
            await new RunAllQualifiersHandler(store).HandleAsync(saveId);

            var reads = new GetQualifierRoundsHandler(store);
            GetQualifierRoundsResponse before = await reads.HandleListAsync(
                saveId, QualifierBoundary.Feeder2Feeder3, (int)SportingColor.Red, fromSeasonNumber: 2);
            before.IsComplete.ShouldBeTrue();
            int roundsBefore = await CountAllQualifierRoundsAsync(store, saveId);
            (long stateBefore, long streamBefore) = await LoadRngAsync(store, saveId);

            var round = await reads.HandleRoundAsync(
                saveId, QualifierBoundary.Feeder2Feeder3, (int)SportingColor.Red, 7, fromSeasonNumber: 2);
            round.RoundNumber.ShouldBe(7);

            GetQualifierRoundsResponse after = await reads.HandleListAsync(
                saveId, QualifierBoundary.Feeder2Feeder3, (int)SportingColor.Red, fromSeasonNumber: 2);
            after.Checksum.ShouldBe(before.Checksum);

            (long stateAfter, long streamAfter) = await LoadRngAsync(store, saveId);
            stateAfter.ShouldBe(stateBefore);
            streamAfter.ShouldBe(streamBefore);
            (await CountAllQualifierRoundsAsync(store, saveId)).ShouldBe(roundsBefore);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    private static async Task PlayAllPerRoundAsync(SaveStore store, Guid saveId)
    {
        var superStep = new PlayQualifierRoundHandler(store);
        for (int i = 0; i < 16; i++)
        {
            await superStep.HandleAsync(saveId).ConfigureAwait(false);
        }

        var feederStep = new PlayFeederQualifierRoundHandler(store);
        foreach ((QualifierBoundary boundary, int? color) in QualifierIdentity.CanonicalOrder)
        {
            if (boundary == QualifierBoundary.Superleague)
            {
                continue;
            }

            for (int i = 0; i < 16; i++)
            {
                PlayFeederQualifierRoundResponse played = await feederStep.HandleAsync(saveId, boundary, color!.Value).ConfigureAwait(false);
                if (i < 15)
                {
                    played.IsComplete.ShouldBeFalse();
                }
                else
                {
                    played.IsComplete.ShouldBeTrue();
                }
            }
        }
    }

    private static async Task PlayMixedThenRunAllAsync(SaveStore store, Guid saveId)
    {
        var superStep = new PlayQualifierRoundHandler(store);
        for (int i = 0; i < 3; i++)
        {
            await superStep.HandleAsync(saveId).ConfigureAwait(false);
        }

        await new RunQualifierHandler(store).HandleAsync(saveId).ConfigureAwait(false);

        var feederStep = new PlayFeederQualifierRoundHandler(store);
        for (int i = 0; i < 5; i++)
        {
            await feederStep.HandleAsync(saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White).ConfigureAwait(false);
        }

        RunAllQualifiersResponse resumed = await new RunAllQualifiersHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        resumed.AlreadyCompleted.ShouldContain("Superleague", StringComparer.Ordinal);
        resumed.ExecutedNow.Count.ShouldBe(16);

        RunAllQualifiersResponse retry = await new RunAllQualifiersHandler(store).HandleAsync(saveId).ConfigureAwait(false);
        retry.AlreadyCompleted.Count.ShouldBe(17);
    }

    private sealed record Snapshot(
        int Standings,
        int Rounds,
        List<string> StandingKeys,
        List<string> RoundKeys,
        long RngState,
        long RngStream);

    private static void AssertSnapshotsEqual(Snapshot expected, Snapshot actual)
    {
        actual.Standings.ShouldBe(expected.Standings);
        actual.Rounds.ShouldBe(expected.Rounds);
        actual.StandingKeys.ShouldBe(expected.StandingKeys);
        actual.RoundKeys.ShouldBe(expected.RoundKeys);
        actual.RngState.ShouldBe(expected.RngState);
        actual.RngStream.ShouldBe(expected.RngStream);
    }

    private static async Task<Snapshot> CaptureAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);
        List<QualifierStandingEntity> standings = await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .OrderBy(e => e.QualifierBoundary).ThenBy(e => e.QualifierSportingColor).ThenBy(e => e.QualifierRank)
            .ToListAsync().ConfigureAwait(false);
        List<QualifierRoundEntity> rounds = await context.QualifierRounds.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .OrderBy(e => e.QualifierBoundary).ThenBy(e => e.QualifierSportingColor).ThenBy(e => e.RoundNumber)
            .ToListAsync().ConfigureAwait(false);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return new Snapshot(
            standings.Count,
            rounds.Count,
            standings.Select(s => $"{s.QualifierBoundary}:{s.QualifierSportingColor}:{s.QualifierRank}:{s.SaveAthleteId}:{s.QualifierScoreThousandths}").ToList(),
            rounds.Select(r => $"{r.QualifierBoundary}:{r.QualifierSportingColor}:{r.RoundNumber}:{r.PayloadChecksum}").ToList(),
            rng.State,
            rng.Stream);
    }

    private static async Task<int> CountRoundsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 3).ConfigureAwait(false);
        return await context.QualifierRounds.CountAsync(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id).ConfigureAwait(false);
    }

    private static async Task<int> CountAllQualifierRoundsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.QualifierRounds.CountAsync().ConfigureAwait(false);
    }

    private static async Task<(long State, long Stream)> LoadRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }
}
