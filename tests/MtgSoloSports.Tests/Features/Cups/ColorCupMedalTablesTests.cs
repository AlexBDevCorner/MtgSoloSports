using MtgSoloSports.Features.Cups.ListCupEditions;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.SimulateSeasons;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Tests.Features.PostseasonTestSaves;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-081: individual and combined Color Cup medal tables by color. The
/// existing team table is untouched; individual medals credit the persisted
/// representation color of each edition and each podium counts once.
/// </summary>
public sealed class ColorCupMedalTablesTests
{
    private static List<ListCupEditionsHandler.Appearance> Appearances(params int[] seasons)
    {
        List<ListCupEditionsHandler.Appearance> appearances = [];
        foreach (int season in seasons)
        {
            foreach (string key in AllKeys())
            {
                appearances.Add(new ListCupEditionsHandler.Appearance(season, key, Name(key)));
            }
        }

        return appearances;
    }

    private static string[] AllKeys() => ["white", "blue", "black", "red", "green", "multicolor", "hybrid", "colorless"];

    private static string Name(string key) => key switch
    {
        "white" => "White",
        "blue" => "Blue",
        "black" => "Black",
        "red" => "Red",
        "green" => "Green",
        "multicolor" => "Multicolor",
        "hybrid" => "Hybrid",
        "colorless" => "Colorless",
        _ => key,
    };

    private static ListCupEditionsHandler.IndividualMedal Medal(int season, string key, int medal) =>
        new(season, key, Name(key), medal);

    [Fact]
    public void Individual_EmptyAppearances_ReturnsEmpty()
    {
        ListCupEditionsHandler.BuildIndividualMedals([], []).ShouldBeEmpty();
        ListCupEditionsHandler.BuildCombinedMedals([], []).ShouldBeEmpty();
    }

    [Fact]
    public void Individual_NoMedalsYet_ListsAllColorsWithZeros()
    {
        var rows = ListCupEditionsHandler.BuildIndividualMedals(Appearances(1), []);

        rows.Count.ShouldBe(8);
        rows.ShouldAllBe(r => r.Gold == 0 && r.Silver == 0 && r.Bronze == 0);
        // Same tie-break as the team table: all zeros sort by team name.
        rows.Select(r => r.TeamName).ShouldBe(rows.Select(r => r.TeamName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Individual_MultipleSeasons_AggregatesOncePerMedal_IncludesMulticolorAndColorless()
    {
        var appearances = Appearances(1, 3);
        var medals = new List<ListCupEditionsHandler.IndividualMedal>
        {
            Medal(1, "white", (int)ColorCupMedal.Gold),
            Medal(1, "blue", (int)ColorCupMedal.Silver),
            Medal(1, "multicolor", (int)ColorCupMedal.Bronze),
            Medal(3, "white", (int)ColorCupMedal.Gold),
            Medal(3, "colorless", (int)ColorCupMedal.Silver),
            Medal(3, "blue", (int)ColorCupMedal.Bronze),
        };

        var rows = ListCupEditionsHandler.BuildIndividualMedals(appearances, medals);
        Dictionary<string, ListCupEditionsResponse.ColorMedalRow> byKey = rows.ToDictionary(r => r.TeamKey, StringComparer.Ordinal);

        byKey["white"].ShouldBe(new ListCupEditionsResponse.ColorMedalRow("white", "White", 2, 0, 0));
        byKey["blue"].ShouldBe(new ListCupEditionsResponse.ColorMedalRow("blue", "Blue", 0, 1, 1));
        byKey["multicolor"].ShouldBe(new ListCupEditionsResponse.ColorMedalRow("multicolor", "Multicolor", 0, 0, 1));
        byKey["colorless"].ShouldBe(new ListCupEditionsResponse.ColorMedalRow("colorless", "Colorless", 0, 1, 0));
        byKey["red"].ShouldBe(new ListCupEditionsResponse.ColorMedalRow("red", "Red", 0, 0, 0));
        rows.Count.ShouldBe(8);
        // White leads on gold; ordering matches team-table rules (gold, silver, bronze, name).
        rows[0].TeamKey.ShouldBe("white");
    }

    [Fact]
    public void Individual_TiedCounts_SortByNameLikeTeamTable()
    {
        var appearances = Appearances(1);
        var medals = new List<ListCupEditionsHandler.IndividualMedal>
        {
            Medal(1, "red", (int)ColorCupMedal.Gold),
            Medal(1, "blue", (int)ColorCupMedal.Silver),
            Medal(1, "white", (int)ColorCupMedal.Bronze),
        };
        // Force a tie: give blue and red identical 1/0/0 by rebuilding medals.
        medals = new List<ListCupEditionsHandler.IndividualMedal>
        {
            Medal(1, "red", (int)ColorCupMedal.Gold),
            Medal(3, "blue", (int)ColorCupMedal.Gold),
        };
        var rows = ListCupEditionsHandler.BuildIndividualMedals(Appearances(1, 3), medals);

        rows[0].TeamName.ShouldBe("Blue");
        rows[1].TeamName.ShouldBe("Red");
    }

    [Fact]
    public void Combined_IsExactSumOfTeamAndIndividual()
    {
        var appearances = Appearances(1, 3);
        var teamResults = new List<ListCupEditionsHandler.Result>
        {
            new(1, "white", "White", 1, "Gold", 1000),
            new(1, "blue", "Blue", 2, "Silver", 900),
            new(1, "red", "Red", 3, "Bronze", 800),
            new(3, "blue", "Blue", 1, "Gold", 1100),
            new(3, "white", "White", 2, "Silver", 1000),
            new(3, "green", "Green", 3, "Bronze", 900),
        };
        var teamTable = ListCupEditionsHandler.BuildTeams(appearances, teamResults);
        var individual = ListCupEditionsHandler.BuildIndividualMedals(appearances, new List<ListCupEditionsHandler.IndividualMedal>
        {
            Medal(1, "white", (int)ColorCupMedal.Gold),
            Medal(1, "red", (int)ColorCupMedal.Silver),
            Medal(1, "blue", (int)ColorCupMedal.Bronze),
            Medal(3, "green", (int)ColorCupMedal.Gold),
        });
        var combined = ListCupEditionsHandler.BuildCombinedMedals(teamTable, individual);

        Dictionary<string, ListCupEditionsResponse.TeamSummary> teams = teamTable.ToDictionary(t => t.TeamKey, StringComparer.Ordinal);
        Dictionary<string, ListCupEditionsResponse.ColorMedalRow> singles = individual.ToDictionary(t => t.TeamKey, StringComparer.Ordinal);
        foreach (var row in combined)
        {
            teams.TryGetValue(row.TeamKey, out var team);
            singles.TryGetValue(row.TeamKey, out var single);
            row.Gold.ShouldBe((team?.Gold ?? 0) + (single?.Gold ?? 0));
            row.Silver.ShouldBe((team?.Silver ?? 0) + (single?.Silver ?? 0));
            row.Bronze.ShouldBe((team?.Bronze ?? 0) + (single?.Bronze ?? 0));
        }

        // One team gold is one medal for its color, irrespective of the
        // four-athlete roster; one individual gold adds exactly one more.
        Dictionary<string, ListCupEditionsResponse.ColorMedalRow> byKey = combined.ToDictionary(r => r.TeamKey, StringComparer.Ordinal);
        byKey["white"].Gold.ShouldBe(teams["white"].Gold + singles["white"].Gold);
        byKey["white"].Gold.ShouldBe(2);
    }

    [Fact]
    public void Combined_SortingMatchesTeamTable()
    {
        var appearances = Appearances(1);
        var teamTable = ListCupEditionsHandler.BuildTeams(appearances, new List<ListCupEditionsHandler.Result>
        {
            new(1, "white", "White", 1, "Gold", 1),
        });
        var individual = ListCupEditionsHandler.BuildIndividualMedals(appearances, new List<ListCupEditionsHandler.IndividualMedal>
        {
            Medal(1, "blue", (int)ColorCupMedal.Gold),
        });
        var combined = ListCupEditionsHandler.BuildCombinedMedals(teamTable, individual);

        // Both colors hold 1 gold; tie-break falls to silver/bronze then name.
        combined[0].TeamName.ShouldBe("Blue");
        combined[1].TeamName.ShouldBe("White");
        combined.Select(r => (r.Gold, r.Silver, r.Bronze))
            .ShouldBe(combined.Select(r => (r.Gold, r.Silver, r.Bronze)).OrderByDescending(m => m));
    }

    [Fact]
    public void TeamTable_UnchangedByMedalTables()
    {
        var appearances = Appearances(1, 3);
        var results = new List<ListCupEditionsHandler.Result>
        {
            new(1, "white", "White", 1, "Gold", 1),
            new(1, "blue", "Blue", 2, "Silver", 1),
            new(1, "red", "Red", 3, "Bronze", 1),
            new(3, "blue", "Blue", 1, "Gold", 1),
            new(3, "white", "White", 2, "Silver", 1),
            new(3, "green", "Green", 3, "Bronze", 1),
        };

        var teams = ListCupEditionsHandler.BuildTeams(appearances, results);
        teams.Sum(t => t.Gold).ShouldBe(2);
        teams.Sum(t => t.Silver).ShouldBe(2);
        teams.Sum(t => t.Bronze).ShouldBe(2);
    }

    [Fact]
    public async Task Handler_RemapAthleteColor_DoesNotMoveIndividualMedal()
    {
        var (store, root, saveId) = await PrepareColorCupAsync(9191UL, 2020UL);
        try
        {
            // Resolve one full Color Cup (individual + team) through the lifecycle.
            await new SimulateSeasonsHandler(store).HandleAsync(saveId, new SimulateSeasonsRequest(1));
            ListCupEditionsResponse before = await new ListCupEditionsHandler(store).HandleAsync(saveId);
            before.ColorIndividualMedals.Sum(r => r.Gold + r.Silver + r.Bronze).ShouldBe(3);

            // Remap every athlete's current sporting color: the individual
            // medal must stay with the persisted representation color.
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                List<SaveAthleteEntity> athletes = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(context.SaveAthletes);
                foreach (SaveAthleteEntity athlete in athletes)
                {
                    athlete.SportingColor = (athlete.SportingColor + 1) % 8;
                }

                await context.SaveChangesAsync();
            }

            ListCupEditionsResponse after = await new ListCupEditionsHandler(store).HandleAsync(saveId);
            after.ColorIndividualMedals.ShouldBe(before.ColorIndividualMedals);
            after.ColorCombinedMedals.ShouldBe(before.ColorCombinedMedals);
            after.ColorTeams.ShouldBe(before.ColorTeams);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Handler_FreshSave_ReturnsEmptyMedalTables()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("No Cups", 1UL, 2UL, Tests.Features.Universe.UniverseTestCatalog.Build());
            ListCupEditionsResponse response = await new ListCupEditionsHandler(store).HandleAsync(created.Detail.SaveId);
            response.ColorIndividualMedals.ShouldBeEmpty();
            response.ColorCombinedMedals.ShouldBeEmpty();
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
