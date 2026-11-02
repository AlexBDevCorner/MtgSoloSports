using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Superleague.PlayQualifierRound;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features;

/// <summary>
/// MSS-122: proves the shared prepared-save templates hand out independent
/// databases, never mutate the published source, and expose the documented
/// sporting boundaries. Small and fast: forks plus status reads, with at most
/// one qualifier step per test as the mutation.
/// </summary>
public sealed class SharedSaveTemplateIsolationTests
{
    [Fact]
    public async Task QualifierPending_ForksAreIndependent()
    {
        var (firstStore, firstRoot, firstId) = await SharedSaveTemplates.ForkSeason2QualifierPendingAsync("mtgsolosports-tmpl-iso-");
        var (secondStore, secondRoot, secondId) = await SharedSaveTemplates.ForkSeason2QualifierPendingAsync("mtgsolosports-tmpl-iso-");
        try
        {
            (long stateBefore, long streamBefore) = await LoadRngAsync(firstStore, firstId);
            (long secondState, long secondStream) = await LoadRngAsync(secondStore, secondId);
            secondState.ShouldBe(stateBefore);
            secondStream.ShouldBe(streamBefore);

            await new PlayQualifierRoundHandler(firstStore).HandleAsync(firstId);

            GetSeasonStatusResponse mutated = await new GetSeasonStatusHandler(firstStore).HandleAsync(firstId);
            mutated.EventProgress!.RoundsPlayed.ShouldBe(1);

            GetSeasonStatusResponse untouched = await new GetSeasonStatusHandler(secondStore).HandleAsync(secondId);
            untouched.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunQualifier]);
            untouched.EventProgress!.RoundsPlayed.ShouldBe(0);
            untouched.EventProgress.TotalRounds.ShouldBe(272);

            (long secondAfter, _) = await LoadRngAsync(secondStore, secondId);
            secondAfter.ShouldBe(stateBefore);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(firstRoot);
            PostseasonTestSaves.DeleteRoot(secondRoot);
        }
    }

    [Fact]
    public async Task QualifierPending_ParallelFirstUse_Agrees()
    {
        Task<(SaveStore Store, string Root, Guid SaveId)>[] forks = Enumerable
            .Range(0, 4)
            .Select(_ => SharedSaveTemplates.ForkSeason2QualifierPendingAsync("mtgsolosports-tmpl-par-"))
            .ToArray();
        (SaveStore Store, string Root, Guid SaveId)[] results = await Task.WhenAll(forks);
        try
        {
            List<string> fingerprints = [];
            foreach ((SaveStore store, _, Guid saveId) in results)
            {
                GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
                status.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunQualifier]);
                status.EventProgress!.RoundsPlayed.ShouldBe(0);
                (long state, long stream) = await LoadRngAsync(store, saveId);
                fingerprints.Add($"{status.ComputedPhase}:{status.EventProgress.TotalRounds}:{state}:{stream}");
            }

            fingerprints.Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        }
        finally
        {
            foreach ((_, string root, _) in results)
            {
                PostseasonTestSaves.DeleteRoot(root);
            }
        }
    }

    [Fact]
    public async Task Season2Complete_ExposesMovement_AsNextAction()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason2CompleteAsync("mtgsolosports-tmpl-s2-");
        try
        {
            GetSeasonStatusResponse before = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            before.LegalNextActions.ShouldBe([SeasonLifecycleActions.ResolveAutomaticMovement]);

            await new MtgSoloSports.Features.Superleague.ResolveAutomaticMovement.ResolveAutomaticMovementHandler(store).HandleAsync(saveId);

            GetSeasonStatusResponse after = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            after.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunQualifier]);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TypeCupSelected_ExposesTeamRun_WithFieldSelected()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkTypeCupSelectedAsync("mtgsolosports-tmpl-type-");
        try
        {
            GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            status.LegalNextActions.ShouldBe([SeasonLifecycleActions.RunTypeCupTeam]);
            status.SourceSeasonNumber.ShouldBe(2);
            status.EventProgress.ShouldNotBeNull();
            status.EventProgress!.RoundsPlayed.ShouldBe(0);
            status.EventProgress.TotalRounds.ShouldBeGreaterThan(0);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 2);
            int selections = await context.TypeCupSelections.AsNoTracking().CountAsync(e => e.SourceSeasonId == season.Id);
            (selections / 4).ShouldBeGreaterThan(32);
            (await context.TypeCupTeamRounds.AsNoTracking().AnyAsync(e => e.SourceSeasonId == season.Id)).ShouldBeFalse();
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    private static async Task<(long State, long Stream)> LoadRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }
}
