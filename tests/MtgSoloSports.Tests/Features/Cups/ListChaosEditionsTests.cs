using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CompleteChaosCup;
using MtgSoloSports.Features.Cups.ListCupEditions;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-111: Chaos editions list in the existing Cups hub endpoint alongside
/// Color/Type history. Partial editions carry no official champion/podium;
/// completed Finals expose P1..P3 plus champion; saves without Chaos show
/// nothing fictitious and Color/Type history stays unchanged.
/// </summary>
public sealed class ListChaosEditionsTests
{
    [Fact]
    public async Task List_FieldSelected_ShowsInProgressWithoutOfficialResults()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-list-partial-");
        try
        {
            await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);

            ListCupEditionsResponse response = await new ListCupEditionsHandler(store).HandleAsync(saveId);

            ListCupEditionsResponse.Edition chaos = response.Editions.Single(e => string.Equals(e.Cup, "Chaos", StringComparison.Ordinal));
            chaos.SourceSeasonNumber.ShouldBe(1);
            chaos.State.ShouldBe("InProgress");
            chaos.TeamCount.ShouldBe(ChaosCupFormat.FieldSize);
            chaos.Podium.ShouldBeEmpty();
            chaos.IndividualChampion.ShouldBeNull();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task List_CompletedCup_ShowsOfficialPodiumAndChampion()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-list-complete-");
        try
        {
            await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            CompleteChaosCupResponse completed = await new CompleteChaosCupHandler(store)
                .CompleteCupAsync(saveId, sourceSeasonNumber: 1);
            completed.IsComplete.ShouldBeTrue();

            ListCupEditionsResponse response = await new ListCupEditionsHandler(store).HandleAsync(saveId);

            ListCupEditionsResponse.Edition chaos = response.Editions.Single(e => string.Equals(e.Cup, "Chaos", StringComparison.Ordinal));
            chaos.SourceSeasonNumber.ShouldBe(1);
            chaos.State.ShouldBe("Completed");
            chaos.TeamCount.ShouldBe(ChaosCupFormat.FieldSize);
            chaos.Podium.Select(p => p.TeamRank).ShouldBe([1, 2, 3]);
            chaos.Podium.Select(p => p.Medal).ShouldBe(["Gold", "Silver", "Bronze"]);
            chaos.IndividualChampion.ShouldNotBeNull();

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            List<ChaosCupGroupStandingEntity> finals = await context.ChaosCupGroupStandings
                .AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == ChaosCupFormat.StageCount - 1)
                .OrderBy(e => e.GroupRank)
                .ToListAsync();
            finals.Count.ShouldBe(ChaosCupFormat.FinalFieldSize);
            for (int i = 0; i < 3; i++)
            {
                chaos.Podium[i].TeamKey.ShouldBe(finals[i].SaveAthleteId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                chaos.Podium[i].TeamScoreThousandths.ShouldBe(finals[i].GroupScoreThousandths);
            }

            chaos.IndividualChampion!.AthleteId.ShouldBe(finals[0].SaveAthleteId);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task List_ColorCupWithoutChaos_ShowsNoFictitiousChaos()
    {
        // MSS-122: shared Color Cup template fork; selection below is incidental setup.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-chaos-list-compat-");
        try
        {
            ListCupEditionsResponse response = await new ListCupEditionsHandler(store).HandleAsync(saveId);

            response.Editions.ShouldNotContain(e => string.Equals(e.Cup, "Chaos", StringComparison.Ordinal));
            ListCupEditionsResponse.Edition color = response.Editions.Single();
            color.Cup.ShouldBe("Color");
            color.State.ShouldBe("Selected");
            color.TeamCount.ShouldBe(8);
            color.Podium.ShouldBeEmpty();
            color.IndividualChampion.ShouldBeNull();
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
