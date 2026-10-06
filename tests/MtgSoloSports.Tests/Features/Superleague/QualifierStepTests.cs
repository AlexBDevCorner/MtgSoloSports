using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Superleague.PlayQualifierRound;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Superleague;

public sealed class QualifierStepTests
{
    [Fact]
    public async Task Step_AllRounds_EqualsOneShot()
    {
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareQualifierAsync(5151UL, 6161UL);
        var (stepStore, stepRoot, stepId) = await PrepareQualifierAsync(5151UL, 6161UL);
        try
        {
            await new RunQualifierHandler(oneShotStore).HandleAsync(oneShotId);
            PlayQualifierRoundHandler step = new(stepStore);
            for (int round = 1; round <= 16; round++)
            {
                PlayQualifierRoundResponse response = await step.HandleAsync(stepId);
                response.Round.RoundNumber.ShouldBe(round);
                response.Round.Group.ShouldBeNull();
                response.Round.Placements.Count.ShouldBe(32);
                response.RoundsPlayed.ShouldBe(round);
                response.TotalRounds.ShouldBe(16);
                response.IsComplete.ShouldBe(round == 16);
            }

            await AssertEquivalentAsync(oneShotStore, oneShotId, stepStore, stepId);
        }
        finally
        {
            DeleteRoot(oneShotRoot);
            DeleteRoot(stepRoot);
        }
    }

    /// <summary>
    /// Golden values for the one-shot runner and this seed. The RNG state dates from
    /// the pre-stepping runner (commit ff73e98); the checksum was re-captured when
    /// bonus became a percentage.
    /// </summary>
    [Fact]
    public async Task OneShot_MatchesPreRefactorGolden()
    {
        var (store, root, saveId) = await PrepareQualifierAsync(5151UL, 6161UL);
        try
        {
            RunQualifierResponse response = await new RunQualifierHandler(store).HandleAsync(saveId);
            response.Checksum.ShouldBe("dc12bcc8557734b41ccf710c51031488a20aa82e99e06d61340c3fb09f2aae96");
            (await LoadRngAsync(store, saveId)).ShouldBe((-1970775041327548483L, 6161L));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_Then_OneShot_EqualsOneShot()
    {
        var (oneShotStore, oneShotRoot, oneShotId) = await PrepareQualifierAsync(5252UL, 6262UL);
        var (mixedStore, mixedRoot, mixedId) = await PrepareQualifierAsync(5252UL, 6262UL);
        try
        {
            await new RunQualifierHandler(oneShotStore).HandleAsync(oneShotId);
            PlayQualifierRoundHandler step = new(mixedStore);
            for (int round = 1; round <= 5; round++)
            {
                await step.HandleAsync(mixedId);
            }

            await new RunQualifierHandler(mixedStore).HandleAsync(mixedId);
            await AssertEquivalentAsync(oneShotStore, oneShotId, mixedStore, mixedId);
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
        var (store, root, saveId) = await PrepareQualifierAsync(5353UL, 6363UL);
        try
        {
            PlayQualifierRoundHandler step = new(store);
            PlayQualifierRoundResponse[] both = await Task.WhenAll(step.HandleAsync(saveId), step.HandleAsync(saveId));
            both.Select(r => r.Round.RoundNumber).OrderBy(n => n).ShouldBe([1, 2]);
            using SaveDbContext context = store.OpenDbContext(saveId);
            (await context.QualifierRounds.CountAsync()).ShouldBe(2);
            (await context.QualifierStandings.CountAsync()).ShouldBe(0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_AfterRngMoved_Aborts()
    {
        var (store, root, saveId) = await PrepareQualifierAsync(5454UL, 6464UL);
        try
        {
            PlayQualifierRoundHandler step = new(store);
            await step.HandleAsync(saveId);
            await MoveRngAsync(store, saveId);

            await Should.ThrowAsync<InvalidOperationException>(() => step.HandleAsync(saveId));
            await Should.ThrowAsync<InvalidOperationException>(() => new RunQualifierHandler(store).HandleAsync(saveId));
            using SaveDbContext after = store.OpenDbContext(saveId);
            (await after.QualifierRounds.CountAsync()).ShouldBe(1);
            (await after.QualifierStandings.CountAsync()).ShouldBe(0);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Step_WithRoundGap_Aborts()
    {
        var (store, root, saveId) = await PrepareQualifierAsync(5555UL, 6565UL);
        try
        {
            PlayQualifierRoundHandler step = new(store);
            await step.HandleAsync(saveId);
            await step.HandleAsync(saveId);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                QualifierRoundEntity first = await context.QualifierRounds.SingleAsync(e => e.RoundNumber == 1);
                context.QualifierRounds.Remove(first);
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
        var (store, root, saveId) = await PrepareQualifierAsync(5656UL, 6666UL);
        try
        {
            await new RunQualifierHandler(store).HandleAsync(saveId);
            await Should.ThrowAsync<RunQualifierConflictException>(() => new PlayQualifierRoundHandler(store).HandleAsync(saveId));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task AssertEquivalentAsync(SaveStore leftStore, Guid leftId, SaveStore rightStore, Guid rightId)
    {
        (await SnapshotAsync(leftStore, leftId).ConfigureAwait(false)).ShouldBe(await SnapshotAsync(rightStore, rightId).ConfigureAwait(false));
    }

    /// <summary>Every sporting output of the qualifier, flattened for exact comparison.</summary>
    private static async Task<List<string>> SnapshotAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<string> lines = [];
        lines.AddRange(await context.QualifierRounds.OrderBy(e => e.RoundNumber)
            .Select(e => "round:" + e.PayloadJson).ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.QualifierStandings.OrderBy(e => e.QualifierRank)
            .Select(e => $"standing:{e.SaveAthleteId}:{e.QualifierRank}:{e.QualifierScoreThousandths}:{e.IsQualified}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.SeasonMemberships.OrderBy(e => e.SeasonId).ThenBy(e => e.SaveAthleteId)
            .Select(e => $"member:{e.SeasonId}:{e.SaveAthleteId}:{e.LeagueId}").ToListAsync().ConfigureAwait(false));
        lines.Add("stories:" + await context.StoryEvents.CountAsync().ConfigureAwait(false));
        (long state, long stream) = await LoadRngAsync(store, saveId).ConfigureAwait(false);
        lines.Add($"rng:{state}:{stream}");
        lines.Add("phase:" + (await context.SaveMetadata.SingleAsync().ConfigureAwait(false)).Phase);
        return lines;
    }
}
