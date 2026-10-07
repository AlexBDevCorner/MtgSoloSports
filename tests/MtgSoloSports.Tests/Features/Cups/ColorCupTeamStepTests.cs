using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.PlayColorCupTeamRound;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class ColorCupTeamStepTests
{
    /// <summary>
    /// Golden values for the one-shot runner and this seed. The RNG state dates from the
    /// pre-stepping runner (commit ff73e98); the checksum was re-captured when bonus became a percentage.
    /// MSS-064 re-captured for league-strength-aware selection (identical results now
    /// score Super > F1 > F2 > F3, so the selected 32 differ from the raw-points field).
    /// </summary>
    [Fact]
    public async Task OneShot_MatchesPreRefactorGolden()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(9191UL, 9292UL);
        try
        {
            RunColorCupTeamResponse response = await new RunColorCupTeamHandler(store).HandleAsync(saveId);
            response.Checksum.ShouldBe("767087c9afd63b750f10f62a51dbc78ee4fcee70f4d8b0404775670bb8de2ad4");
            (await LoadRngAsync(store, saveId)).ShouldBe((-3128078357953407831L, 9292L));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_AllRounds_EqualsOneShot()
    {
        // MSS-067: forked copy instead of a second identical Season 1 simulation.
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareColorCupAsync(9191UL, 9292UL);
        var (stepStore, stepRoot, stepId) = await TestSaveStores.ForkAsync(oneShotStore, oneShotId, "mtgsolosports-cup-team-step-");
        try
        {
            await new RunColorCupTeamHandler(oneShotStore).HandleAsync(oneShotId);
            PlayColorCupTeamRoundHandler step = new(stepStore);
            for (int index = 0; index < 32; index++)
            {
                PlayColorCupTeamRoundResponse response = await step.HandleAsync(stepId);
                response.Round.Group.ShouldBe(index / 8 + 1);
                response.Round.RoundNumber.ShouldBe(index % 8 + 1);
                response.RoundsPlayed.ShouldBe(index + 1);
                response.TotalRounds.ShouldBe(32);
                response.IsComplete.ShouldBe(index == 31);
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
        // MSS-067: forked copy instead of a second identical Season 1 simulation.
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareColorCupAsync(9393UL, 9494UL);
        var (mixedStore, mixedRoot, mixedId) = await TestSaveStores.ForkAsync(oneShotStore, oneShotId, "mtgsolosports-cup-team-mixed-");
        try
        {
            await new RunColorCupTeamHandler(oneShotStore).HandleAsync(oneShotId);
            PlayColorCupTeamRoundHandler step = new(mixedStore);
            for (int round = 1; round <= 11; round++)
            {
                await step.HandleAsync(mixedId);
            }

            await new RunColorCupTeamHandler(mixedStore).HandleAsync(mixedId);
            (await SnapshotAsync(mixedStore, mixedId)).ShouldBe(await SnapshotAsync(oneShotStore, oneShotId));
        }
        finally
        {
            DeleteRoot(oneShotRoot);
            DeleteRoot(mixedRoot);
        }
    }

    [Fact]
    public async Task Step_GroupBoundary_MatchesOneShotRngChain()
    {
        // MSS-067: forked copy instead of a second identical Season 1 simulation.
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareColorCupAsync(9595UL, 9696UL);
        var (stepStore, stepRoot, stepId) = await TestSaveStores.ForkAsync(oneShotStore, oneShotId, "mtgsolosports-cup-team-bound-");
        try
        {
            await new RunColorCupTeamHandler(oneShotStore).HandleAsync(oneShotId);
            PlayColorCupTeamRoundHandler step = new(stepStore);
            for (int round = 1; round <= 8; round++)
            {
                PlayColorCupTeamRoundResponse response = await step.HandleAsync(stepId);
                response.Round.Group.ShouldBe(1);
                response.Round.RoundNumber.ShouldBe(round);
            }

            using SaveDbContext oneShot = oneShotStore.OpenDbContext(oneShotId);
            ColorCupTeamRoundEntity groupTwoStart = await oneShot.ColorCupTeamRounds
                .SingleAsync(e => e.GroupNumber == 2 && e.RoundNumber == 1);
            (await LoadRngAsync(stepStore, stepId)).ShouldBe((groupTwoStart.RngBeforeState, groupTwoStart.RngBeforeStream));
        }
        finally
        {
            DeleteRoot(oneShotRoot);
            DeleteRoot(stepRoot);
        }
    }

    [Fact]
    public async Task Step_Concurrent_PlaysTwoDistinctRounds()
    {
        // MSS-067: shared template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup-team-step-");
        try
        {
            PlayColorCupTeamRoundHandler step = new(store);
            PlayColorCupTeamRoundResponse[] both = await Task.WhenAll(step.HandleAsync(saveId), step.HandleAsync(saveId));
            both.Select(r => r.Round.RoundNumber).OrderBy(n => n).ShouldBe([1, 2]);
            using SaveDbContext context = store.OpenDbContext(saveId);
            (await context.ColorCupTeamRounds.CountAsync()).ShouldBe(2);
            (await context.ColorCupTeamStandings.CountAsync()).ShouldBe(0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_AfterRngMoved_Aborts()
    {
        // MSS-067: shared template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup-team-step-");
        try
        {
            PlayColorCupTeamRoundHandler step = new(store);
            for (int round = 1; round <= 8; round++)
            {
                await step.HandleAsync(saveId);
            }

            await MoveRngAsync(store, saveId);
            await Should.ThrowAsync<InvalidOperationException>(() => step.HandleAsync(saveId));
            await Should.ThrowAsync<InvalidOperationException>(() => new RunColorCupTeamHandler(store).HandleAsync(saveId));
            using SaveDbContext after = store.OpenDbContext(saveId);
            (await after.ColorCupTeamRounds.CountAsync()).ShouldBe(8);
            (await after.ColorCupTeamStandings.CountAsync()).ShouldBe(0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_WithRoundGap_Aborts()
    {
        // MSS-067: shared template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup-team-step-");
        try
        {
            PlayColorCupTeamRoundHandler step = new(store);
            await step.HandleAsync(saveId);
            await step.HandleAsync(saveId);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                ColorCupTeamRoundEntity first = await context.ColorCupTeamRounds.SingleAsync(e => e.GroupNumber == 1 && e.RoundNumber == 1);
                context.ColorCupTeamRounds.Remove(first);
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
        // MSS-067: shared template forked into an isolated save.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup-team-step-");
        try
        {
            await new RunColorCupTeamHandler(store).HandleAsync(saveId);
            await Should.ThrowAsync<RunColorCupTeamConflictException>(() => new PlayColorCupTeamRoundHandler(store).HandleAsync(saveId));
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
        lines.AddRange(await context.ColorCupTeamRounds.OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber)
            .Select(e => "round:" + e.PayloadJson).ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.ColorCupTeamGroupStandings.OrderBy(e => e.GroupNumber).ThenBy(e => e.GroupRank)
            .Select(e => $"leg:{e.GroupNumber}:{e.SaveAthleteId}:{e.GroupRank}:{e.GroupScoreThousandths}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.ColorCupTeamStandings.OrderBy(e => e.TeamRank)
            .Select(e => $"team:{e.SportingColor}:{e.TeamRank}:{e.TeamScoreThousandths}:{e.Medal}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.Honours.OrderBy(e => e.Id)
            .Select(e => $"honour:{e.Kind}:{e.SaveAthleteId}").ToListAsync().ConfigureAwait(false));
        lines.Add("stories:" + await context.StoryEvents.CountAsync().ConfigureAwait(false));
        (long state, long stream) = await LoadRngAsync(store, saveId).ConfigureAwait(false);
        lines.Add($"rng:{state}:{stream}");
        lines.Add("phase:" + (await context.SaveMetadata.SingleAsync().ConfigureAwait(false)).Phase);
        return lines;
    }
}
