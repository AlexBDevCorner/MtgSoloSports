using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.PlayTypeCupTeamRound;
using MtgSoloSports.Features.Cups.RunTypeCupTeam;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class TypeCupTeamStepTests
{
    /// <summary>Golden values from the pre-stepping runner (commit ff73e98) for the same seed.</summary>
    [Fact]
    public async Task OneShot_MatchesPreRefactorGolden()
    {
        var (store, root, saveId) = await PrepareTypeCupAsync(2323UL, 3434UL);
        try
        {
            RunTypeCupTeamResponse response = await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            response.Checksum.ShouldBe("5a2216de4488c02c2dc267331997ca36530644dc1df9e9da2a2887cce82976c3");
            (await LoadRngAsync(store, saveId)).ShouldBe((-966732255112031938L, 3434L));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_AllRounds_EqualsOneShot()
    {
        // MSS-067: forked copy instead of building the same seeded save twice.
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareTypeCupAsync(2323UL, 3434UL);
        var (stepStore, stepRoot, stepId) = await TestSaveStores.ForkAsync(oneShotStore, oneShotId, "mtgsolosports-type-step-");
        try
        {
            await new RunTypeCupTeamHandler(oneShotStore).HandleAsync(oneShotId, sourceSeasonNumber: 2);
            PlayTypeCupTeamRoundHandler step = new(stepStore);
            for (int index = 0; index < 32; index++)
            {
                PlayTypeCupTeamRoundResponse response = await step.HandleAsync(stepId, sourceSeasonNumber: 2);
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
        // MSS-067: forked copy instead of building the same seeded save twice.
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareTypeCupAsync(9393UL, 9494UL);
        var (mixedStore, mixedRoot, mixedId) = await TestSaveStores.ForkAsync(oneShotStore, oneShotId, "mtgsolosports-type-mixed-");
        try
        {
            await new RunTypeCupTeamHandler(oneShotStore).HandleAsync(oneShotId, sourceSeasonNumber: 2);
            PlayTypeCupTeamRoundHandler step = new(mixedStore);
            for (int round = 1; round <= 11; round++)
            {
                await step.HandleAsync(mixedId, sourceSeasonNumber: 2);
            }

            await new RunTypeCupTeamHandler(mixedStore).HandleAsync(mixedId, sourceSeasonNumber: 2);
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
        // MSS-067: forked copy instead of building the same seeded save twice.
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareTypeCupAsync(9595UL, 9696UL);
        var (stepStore, stepRoot, stepId) = await TestSaveStores.ForkAsync(oneShotStore, oneShotId, "mtgsolosports-type-bound-");
        try
        {
            await new RunTypeCupTeamHandler(oneShotStore).HandleAsync(oneShotId, sourceSeasonNumber: 2);
            PlayTypeCupTeamRoundHandler step = new(stepStore);
            for (int round = 1; round <= 8; round++)
            {
                PlayTypeCupTeamRoundResponse response = await step.HandleAsync(stepId, sourceSeasonNumber: 2);
                response.Round.Group.ShouldBe(1);
                response.Round.RoundNumber.ShouldBe(round);
            }

            using SaveDbContext oneShot = oneShotStore.OpenDbContext(oneShotId);
            TypeCupTeamRoundEntity groupTwoStart = await oneShot.TypeCupTeamRounds
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
        var (store, root, saveId) = await PrepareTypeCupAsync(9797UL, 9898UL);
        try
        {
            PlayTypeCupTeamRoundHandler step = new(store);
            PlayTypeCupTeamRoundResponse[] both = await Task.WhenAll(step.HandleAsync(saveId, sourceSeasonNumber: 2), step.HandleAsync(saveId, sourceSeasonNumber: 2));
            both.Select(r => r.Round.RoundNumber).OrderBy(n => n).ShouldBe([1, 2]);
            using SaveDbContext context = store.OpenDbContext(saveId);
            (await context.TypeCupTeamRounds.CountAsync()).ShouldBe(2);
            (await context.TypeCupTeamStandings.CountAsync()).ShouldBe(0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_AfterRngMoved_Aborts()
    {
        var (store, root, saveId) = await PrepareTypeCupAsync(9999UL, 1010UL);
        try
        {
            PlayTypeCupTeamRoundHandler step = new(store);
            for (int round = 1; round <= 8; round++)
            {
                await step.HandleAsync(saveId, sourceSeasonNumber: 2);
            }

            await MoveRngAsync(store, saveId);
            await Should.ThrowAsync<InvalidOperationException>(() => step.HandleAsync(saveId, sourceSeasonNumber: 2));
            await Should.ThrowAsync<InvalidOperationException>(() => new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2));
            using SaveDbContext after = store.OpenDbContext(saveId);
            (await after.TypeCupTeamRounds.CountAsync()).ShouldBe(8);
            (await after.TypeCupTeamStandings.CountAsync()).ShouldBe(0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_WithRoundGap_Aborts()
    {
        var (store, root, saveId) = await PrepareTypeCupAsync(1111UL, 1212UL);
        try
        {
            PlayTypeCupTeamRoundHandler step = new(store);
            await step.HandleAsync(saveId, sourceSeasonNumber: 2);
            await step.HandleAsync(saveId, sourceSeasonNumber: 2);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                TypeCupTeamRoundEntity first = await context.TypeCupTeamRounds.SingleAsync(e => e.GroupNumber == 1 && e.RoundNumber == 1);
                context.TypeCupTeamRounds.Remove(first);
                await context.SaveChangesAsync();
            }

            await Should.ThrowAsync<InvalidOperationException>(() => step.HandleAsync(saveId, sourceSeasonNumber: 2));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_AfterCompletion_Conflicts()
    {
        var (store, root, saveId) = await PrepareTypeCupAsync(1313UL, 1414UL);
        try
        {
            await new RunTypeCupTeamHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            await Should.ThrowAsync<RunTypeCupTeamConflictException>(() => new PlayTypeCupTeamRoundHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2));
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
        lines.AddRange(await context.TypeCupTeamRounds.OrderBy(e => e.GroupNumber).ThenBy(e => e.RoundNumber)
            .Select(e => "round:" + e.PayloadJson).ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.TypeCupTeamGroupStandings.OrderBy(e => e.GroupNumber).ThenBy(e => e.GroupRank)
            .Select(e => $"leg:{e.GroupNumber}:{e.SaveAthleteId}:{e.GroupRank}:{e.GroupScoreThousandths}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.TypeCupTeamStandings.OrderBy(e => e.TeamRank)
            .Select(e => $"team:{e.CreatureType}:{e.TeamRank}:{e.TeamScoreThousandths}:{e.Medal}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.SaveAthletes.Where(e => e.TypeCupNationality != null).OrderBy(e => e.Id)
            .Select(e => $"nationality:{e.Id}:{e.TypeCupNationality}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.Honours.OrderBy(e => e.Id)
            .Select(e => $"honour:{e.Kind}:{e.SaveAthleteId}").ToListAsync().ConfigureAwait(false));
        lines.Add("stories:" + await context.StoryEvents.CountAsync().ConfigureAwait(false));
        (long state, long stream) = await LoadRngAsync(store, saveId).ConfigureAwait(false);
        lines.Add($"rng:{state}:{stream}");
        lines.Add("phase:" + (await context.SaveMetadata.SingleAsync().ConfigureAwait(false)).Phase);
        return lines;
    }
}
