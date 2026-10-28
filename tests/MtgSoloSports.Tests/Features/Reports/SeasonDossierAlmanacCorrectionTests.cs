using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Cups.RunColorCupTeam;
using MtgSoloSports.Features.Records.GetHallOfFame;
using MtgSoloSports.Features.Reports.SeasonDossier;
using MtgSoloSports.Features.Reports.UniverseAlmanac;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Reports;

/// <summary>
/// MSS-088 correction: regression coverage for three P1 defects —
/// partial Color Cup medal counting, individual podium athlete identities,
/// and streak leaders outside the weighted top 20.
/// </summary>
public sealed class SeasonDossierAlmanacCorrectionTests
{
    [Fact]
    public async Task Almanac_IndividualOnly_DoesNotCountTowardMedalTables()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(9293UL, 3031UL);
        try
        {
            await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                AlmanacCupTables pending = await UniverseAlmanacCupQueries.LoadCupTablesAsync(
                    context, new Dictionary<int, string>(), CancellationToken.None);
                pending.Editions.Single().State.ShouldBe("InProgress");
                pending.ColorIndividualMedals.Sum(r => r.Gold + r.Silver + r.Bronze).ShouldBe(0);
                pending.ColorCombinedMedals.Sum(r => r.Gold + r.Silver + r.Bronze).ShouldBe(
                    pending.ColorTeamTable.Sum(t => t.Gold + t.Silver + t.Bronze));
            }

            await new RunColorCupTeamHandler(store).HandleAsync(saveId);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                AlmanacCupTables done = await UniverseAlmanacCupQueries.LoadCupTablesAsync(
                    context, new Dictionary<int, string>(), CancellationToken.None);
                done.Editions.Single().State.ShouldBe("Completed");
                done.ColorIndividualMedals.Sum(r => r.Gold + r.Silver + r.Bronze).ShouldBe(3);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Dossier_MapIndividual_PreservesSilverAndBronzeIdentities()
    {
        List<GetColorCupIndividualMember> standings =
        [
            new(11, "Gold Card", "White", 1, 1, 9000, 8000, 3, "Gold", null),
            new(22, "Silver Card", "Blue", 2, 2, 8000, 7000, 2, "Silver", null),
            new(33, "Bronze Card", "Red", 3, 3, 7000, 6000, 1, "Bronze", null),
        ];
        var response = new GetColorCupIndividualResultResponse(
            Guid.NewGuid(), 1, 1, 3, 1, "checksum",
            0UL, 0UL, 0UL, 0UL,
            11, "Gold Card", standings);

        IndividualCupSummary? summary = SeasonDossierCupQueries.MapIndividual(response);

        summary.ShouldNotBeNull();
        summary.TopThree.Count.ShouldBe(3);
        summary.TopThree[0].AthleteId.ShouldBe(11);
        summary.TopThree[0].AthleteName.ShouldBe("Gold Card");
        summary.TopThree[1].AthleteId.ShouldBe(22);
        summary.TopThree[1].AthleteName.ShouldBe("Silver Card");
        summary.TopThree[2].AthleteId.ShouldBe(33);
        summary.TopThree[2].AthleteName.ShouldBe("Bronze Card");
        summary.ChampionAthleteId.ShouldBe(11);
    }

    [Fact]
    public void Streaks_FindsHolderOutsideWeightedTop20()
    {
        List<HallOfFameEntry> full = [];
        for (int i = 1; i <= 20; i++)
        {
            full.Add(new HallOfFameEntry(
                i, i, $"Top {i}", 0, "White",
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                1, 1,
                10000 - i, 10000 - i, 0, 0, 0, 0, 0, 0, 0, 1));
        }

        full.Add(new HallOfFameEntry(
            21, 999, "Streak Holder", 1, "Blue",
            0, 5, 5, 10, 10, 5, 5, 5, 0, 0, 0,
            5, 6,
            1, 1, 0, 0, 0, 0, 0, 0, 0, 1));

        IReadOnlyList<HallOfFameEntry> truncated = full.Take(20).ToList();
        (IReadOnlyList<StreakEntry> truncatedTitles, IReadOnlyList<StreakEntry> truncatedStages) =
            UniverseAlmanacLeaderQueries.BuildStreaks(truncated);
        truncatedTitles.ShouldBeEmpty();
        truncatedStages.ShouldBeEmpty();

        (IReadOnlyList<StreakEntry> titles, IReadOnlyList<StreakEntry> stages) =
            UniverseAlmanacLeaderQueries.BuildStreaks(full);
        titles.Single().AthleteId.ShouldBe(999);
        titles.Single().Streak.ShouldBe(5);
        stages.Single().AthleteId.ShouldBe(999);
        stages.Single().Streak.ShouldBe(6);
    }
}
