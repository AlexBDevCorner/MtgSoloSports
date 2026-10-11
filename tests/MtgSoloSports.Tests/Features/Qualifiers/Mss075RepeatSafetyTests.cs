using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using MtgSoloSports.Features.Seasons.SeasonLifecycle;
using MtgSoloSports.Features.Superleague.RebalanceFeeders;
using MtgSoloSports.Features.Superleague.RunQualifier;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Leagues;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Qualifiers;

/// <summary>
/// MSS-075: repeating completed bulk/single qualifier runs never duplicates.
/// </summary>
public sealed class Mss075RepeatSafetyTests
{
    [Fact]
    public async Task RepeatBulkAndSingle_DoNotDuplicateOrOverwrite()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-mss075-repeat-");
        try
        {
            await PostseasonTestSaves.DrainToActionAsync(store, saveId, "RunQualifier");
            RunAllQualifiersResponse first = await new RunAllQualifiersHandler(store).HandleAsync(saveId);
            first.ExecutedNow.Count.ShouldBe(17);

            (string checksumBefore, long rngState, long rngStream) = await LoadFingerprintsAsync(store, saveId, first.FromSeasonNumber);

            RunAllQualifiersResponse repeat = await new RunAllQualifiersHandler(store).HandleAsync(saveId);
            repeat.ExecutedNow.Count.ShouldBe(0);
            repeat.AlreadyCompleted.Count.ShouldBe(17);
            repeat.TotalStandings.ShouldBe(288);
            repeat.TotalRounds.ShouldBe(272);

            (string checksumAfter, long rngStateAfter, long rngStreamAfter) = await LoadFingerprintsAsync(store, saveId, first.FromSeasonNumber);
            checksumAfter.ShouldBe(checksumBefore);
            rngStateAfter.ShouldBe(rngState);
            rngStreamAfter.ShouldBe(rngStream);

            await Should.ThrowAsync<RunQualifierConflictException>(() =>
                new RunQualifierHandler(store).HandleAsync(saveId));

            await Should.ThrowAsync<RunFeederQualifierConflictException>(() =>
                new BoundaryQualifierRunner(store).HandleAsync(
                    saveId, QualifierBoundary.Feeder1Feeder2, (int)SportingColor.White));

            GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            status.QualifierResolved.ShouldBeTrue();
            status.LegalNextActions.ShouldBe([SeasonLifecycleActions.RebalanceFeeders]);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RepeatBulk_AfterRebalance_DoesNotRewritePhaseBackwards()
    {
        // MSS-075 correction P1: repeating run-all after feeder rebalance must
        // stay a no-op success without moving the persisted phase backwards to
        // QualifierResolved and without touching RNG/results.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-mss075-repeat-rebal-");
        try
        {
            await PostseasonTestSaves.DrainToActionAsync(store, saveId, "RunQualifier");
            RunAllQualifiersResponse first = await new RunAllQualifiersHandler(store).HandleAsync(saveId);
            await new RebalanceFeedersHandler(store).HandleAsync(saveId);

            (string checksumBefore, long rngState, long rngStream) = await LoadFingerprintsAsync(store, saveId, first.FromSeasonNumber);
            string phaseBefore = await LoadPhaseAsync(store, saveId);

            phaseBefore.ShouldBe(SavePhaseParser.ToText(SavePhase.Rebalanced));

            RunAllQualifiersResponse repeat = await new RunAllQualifiersHandler(store).HandleAsync(saveId);
            repeat.ExecutedNow.Count.ShouldBe(0);
            repeat.AlreadyCompleted.Count.ShouldBe(17);

            (string checksumAfter, long rngStateAfter, long rngStreamAfter) = await LoadFingerprintsAsync(store, saveId, first.FromSeasonNumber);
            checksumAfter.ShouldBe(checksumBefore);
            rngStateAfter.ShouldBe(rngState);
            rngStreamAfter.ShouldBe(rngStream);

            string phaseAfter = await LoadPhaseAsync(store, saveId);
            phaseAfter.ShouldBe(SavePhaseParser.ToText(SavePhase.Rebalanced));

            GetSeasonStatusResponse status = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            status.Rebalanced.ShouldBeTrue();
            status.QualifierResolved.ShouldBeTrue();
            status.QualifierProgress.ShouldNotBeNull();
            status.QualifierProgress!.CompletedEvents.ShouldBe(17);
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    private static async Task<string> LoadPhaseAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await context.SaveMetadata.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return metadata.Phase;
    }

    private static async Task<(string Checksum, long RngState, long RngStream)> LoadFingerprintsAsync(
        SaveStore store, Guid saveId, int fromSeason)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == fromSeason).ConfigureAwait(false);
        SeasonEntity next = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == fromSeason + 1).ConfigureAwait(false);
        List<QualifierStandingEntity> standings = await context.QualifierStandings.AsNoTracking()
            .Where(e => e.FromSeasonId == source.Id && e.ToSeasonId == next.Id)
            .OrderBy(e => e.QualifierBoundary).ThenBy(e => e.QualifierSportingColor).ThenBy(e => e.QualifierRank)
            .ToListAsync().ConfigureAwait(false);
        string checksum = GetQualifierListHandler.ComputeChecksum(standings);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (checksum, rng.State, rng.Stream);
    }
}
