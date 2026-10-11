using MtgSoloSports.Features.Qualifiers;
using MtgSoloSports.Features.Seasons.GetSeasonStatus;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Qualifiers;

/// <summary>
/// MSS-075: completed qualifier progress survives reload, refresh and resume.
/// </summary>
public sealed class Mss075ResumeTests
{
    [Fact]
    public async Task CompletedSave_SurvivesReloadAndResume()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-mss075-resume-");
        try
        {
            await PostseasonTestSaves.DrainToActionAsync(store, saveId, "RunQualifier");
            RunAllQualifiersResponse runAll = await new RunAllQualifiersHandler(store).HandleAsync(saveId);

            GetSeasonStatusResponse first = await new GetSeasonStatusHandler(store).HandleAsync(saveId);
            GetQualifierListResponse firstList = await new GetQualifierListHandler(store).HandleAsync(
                saveId, fromSeasonNumber: runAll.FromSeasonNumber);

            var (secondStore, secondRoot, secondId) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-mss075-resume2-");
            try
            {
                await AssertResumedAsync(secondStore, secondId, first, firstList, runAll);
            }
            finally
            {
                TestSaveStores.DeleteRoot(secondRoot);
            }
        }
        finally
        {
            PostseasonTestSaves.DeleteRoot(root);
        }
    }

    private static async Task AssertResumedAsync(
        Persistence.Saves.SaveStore secondStore,
        Guid secondId,
        GetSeasonStatusResponse first,
        GetQualifierListResponse firstList,
        RunAllQualifiersResponse runAll)
    {
        GetSeasonStatusResponse second = await new GetSeasonStatusHandler(secondStore).HandleAsync(secondId).ConfigureAwait(false);
        second.ComputedPhase.ShouldBe(first.ComputedPhase);
        second.QualifierResolved.ShouldBe(first.QualifierResolved);
        second.LegalNextActions.ShouldBe(first.LegalNextActions);

        GetQualifierListResponse secondList = await new GetQualifierListHandler(secondStore).HandleAsync(
            secondId, fromSeasonNumber: runAll.FromSeasonNumber).ConfigureAwait(false);
        secondList.Events.Count.ShouldBe(firstList.Events.Count);
        secondList.Events.Select(e => e.Checksum).ShouldBe(firstList.Events.Select(e => e.Checksum));
    }
}
