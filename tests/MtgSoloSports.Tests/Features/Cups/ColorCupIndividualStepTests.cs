using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.PlayColorCupIndividualRound;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class ColorCupIndividualStepTests
{
    /// <summary>
    /// Golden values for the one-shot runner and this seed. The RNG state dates from the
    /// pre-stepping runner (commit ff73e98); the checksum was re-captured when bonus became a percentage.
    /// MSS-064 re-captured for league-strength-aware selection.
    /// </summary>
    [Fact]
    public async Task OneShot_MatchesPreRefactorGolden()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(7171UL, 8181UL);
        try
        {
            RunColorCupIndividualResponse response = await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            response.Checksum.ShouldBe("a4732eb16ac167642d69092c8caa382bd32a894ee5aa59700131fca31d1fe8d6");
            (await LoadRngAsync(store, saveId)).ShouldBe((5256838557070595265L, 8181L));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_AllRounds_EqualsOneShot()
    {
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareColorCupAsync(7171UL, 8181UL);
        var (stepStore, stepRoot, stepId) = await PrepareColorCupAsync(7171UL, 8181UL);
        try
        {
            await new RunColorCupIndividualHandler(oneShotStore).HandleAsync(oneShotId);
            PlayColorCupIndividualRoundHandler step = new(stepStore);
            for (int round = 1; round <= 16; round++)
            {
                PlayColorCupIndividualRoundResponse response = await step.HandleAsync(stepId);
                response.Round.RoundNumber.ShouldBe(round);
                response.Round.Group.ShouldBeNull();
                response.Round.Placements.Count.ShouldBe(32);
                response.RoundsPlayed.ShouldBe(round);
                response.TotalRounds.ShouldBe(16);
                response.IsComplete.ShouldBe(round == 16);
            }

            (await SnapshotAsync(stepStore, stepId)).ShouldBe(await SnapshotAsync(oneShotStore, oneShotId));
        }
        finally
        {
            DeleteRoot(oneShotRoot);
            DeleteRoot(stepRoot);
        }
    }

    [Fact]
    public async Task Step_Then_OneShot_EqualsOneShot()
    {
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareColorCupAsync(7272UL, 8282UL);
        var (mixedStore, mixedRoot, mixedId) = await PrepareColorCupAsync(7272UL, 8282UL);
        try
        {
            await new RunColorCupIndividualHandler(oneShotStore).HandleAsync(oneShotId);
            PlayColorCupIndividualRoundHandler step = new(mixedStore);
            for (int round = 1; round <= 7; round++)
            {
                await step.HandleAsync(mixedId);
            }

            await new RunColorCupIndividualHandler(mixedStore).HandleAsync(mixedId);
            (await SnapshotAsync(mixedStore, mixedId)).ShouldBe(await SnapshotAsync(oneShotStore, oneShotId));
        }
        finally
        {
            DeleteRoot(oneShotRoot);
            DeleteRoot(mixedRoot);
        }
    }

    [Fact]
    public async Task Step_Concurrent_PlaysTwoDistinctRounds()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(7373UL, 8383UL);
        try
        {
            PlayColorCupIndividualRoundHandler step = new(store);
            PlayColorCupIndividualRoundResponse[] both = await Task.WhenAll(step.HandleAsync(saveId), step.HandleAsync(saveId));
            both.Select(r => r.Round.RoundNumber).OrderBy(n => n).ShouldBe([1, 2]);
            using SaveDbContext context = store.OpenDbContext(saveId);
            (await context.ColorCupIndividualRounds.CountAsync()).ShouldBe(2);
            (await context.ColorCupIndividualStandings.CountAsync()).ShouldBe(0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_AfterRngMoved_Aborts()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(7474UL, 8484UL);
        try
        {
            PlayColorCupIndividualRoundHandler step = new(store);
            await step.HandleAsync(saveId);
            await MoveRngAsync(store, saveId);

            await Should.ThrowAsync<InvalidOperationException>(() => step.HandleAsync(saveId));
            await Should.ThrowAsync<InvalidOperationException>(() => new RunColorCupIndividualHandler(store).HandleAsync(saveId));
            using SaveDbContext after = store.OpenDbContext(saveId);
            (await after.ColorCupIndividualRounds.CountAsync()).ShouldBe(1);
            (await after.ColorCupIndividualStandings.CountAsync()).ShouldBe(0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_WithRoundGap_Aborts()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(7575UL, 8585UL);
        try
        {
            PlayColorCupIndividualRoundHandler step = new(store);
            await step.HandleAsync(saveId);
            await step.HandleAsync(saveId);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                ColorCupIndividualRoundEntity first = await context.ColorCupIndividualRounds.SingleAsync(e => e.RoundNumber == 1);
                context.ColorCupIndividualRounds.Remove(first);
                await context.SaveChangesAsync();
            }

            await Should.ThrowAsync<InvalidOperationException>(() => step.HandleAsync(saveId));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_AfterCompletion_Conflicts()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(7676UL, 8686UL);
        try
        {
            await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            await Should.ThrowAsync<RunColorCupIndividualConflictException>(() => new PlayColorCupIndividualRoundHandler(store).HandleAsync(saveId));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>Every sporting output of the event, flattened for exact comparison.</summary>
    private static async Task<List<string>> SnapshotAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<string> lines = [];
        lines.AddRange(await context.ColorCupIndividualRounds.OrderBy(e => e.RoundNumber)
            .Select(e => "round:" + e.PayloadJson).ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.ColorCupIndividualStandings.OrderBy(e => e.CupRank)
            .Select(e => $"standing:{e.SaveAthleteId}:{e.CupRank}:{e.CupScoreThousandths}:{e.Medal}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.Honours.OrderBy(e => e.Id)
            .Select(e => $"honour:{e.Kind}:{e.SaveAthleteId}").ToListAsync().ConfigureAwait(false));
        lines.Add("stories:" + await context.StoryEvents.CountAsync().ConfigureAwait(false));
        (long state, long stream) = await LoadRngAsync(store, saveId).ConfigureAwait(false);
        lines.Add($"rng:{state}:{stream}");
        lines.Add("phase:" + (await context.SaveMetadata.SingleAsync().ConfigureAwait(false)).Phase);
        return lines;
    }
}
