using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;
using static MtgSoloSports.Features.Cups.CupHistory.CupTeamHistoryBuilder;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class CupTeamHistoryBuilderTests
{
    private static readonly Guid Save = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Build_TwoEditions_AggregatesHonoursSquadsAndRoster()
    {
        CupTeamHistoryResponse response = Build(TwoEditions());

        response.TeamKey.ShouldBe("red");
        response.TeamName.ShouldBe("Red");
        response.Cup.ShouldBe("Color");
        response.Seasons.Select(s => s.SourceSeasonNumber).ShouldBe([3, 1]);

        response.Honours.Editions.ShouldBe(2);
        response.Honours.Gold.ShouldBe(1);
        response.Honours.Silver.ShouldBe(0);
        response.Honours.Bronze.ShouldBe(1);
        response.Honours.BestRank.ShouldBe(1);
        response.Honours.BestRankSeasonNumber.ShouldBe(3);
        response.Honours.GroupWins.ShouldBe(3);
        response.Honours.RoundWins.ShouldBe(11);
        response.Honours.TotalScoreThousandths.ShouldBe(900_000L);

        CupTeamHistoryResponse.Season latest = response.Seasons[0];
        latest.State.ShouldBe("Completed");
        latest.TeamRank.ShouldBe(1);
        latest.TeamCount.ShouldBe(8);
        latest.Medal.ShouldBe("Gold");
        latest.Squad.Select(m => m.AthleteId).ShouldBe([1, 2, 3, 5]);
        latest.Squad.Select(m => m.SelectionRank).ShouldBe([1, 2, 3, 4]);
        latest.Squad[0].Leg!.GroupNumber.ShouldBe(1);
        latest.Squad[0].Leg!.GroupRank.ShouldBe(1);
        latest.Squad[0].Leg!.GroupSize.ShouldBe(8);
        latest.Squad[0].Individual!.Medal.ShouldBe("Gold");
        latest.Squad[3].ImageUrl.ShouldBeNull();

        response.Roster.Select(r => $"{r.AthleteId}:{r.Caps}").ShouldBe(["1:2", "2:2", "3:2", "4:1", "5:1"]);
        CupTeamHistoryResponse.RosterEntry first = response.Roster[0];
        first.FirstSeasonNumber.ShouldBe(1);
        first.LastSeasonNumber.ShouldBe(3);
        first.TotalLegScoreThousandths.ShouldBe(250_000L);
        first.BestGroupRank.ShouldBe(1);
        first.BestSelectionRank.ShouldBe(1);

        response.IndividualMedals.Select(m => $"{m.SourceSeasonNumber}:{m.AthleteId}:{m.Medal}")
            .ShouldBe(["3:1:Gold", "1:2:Bronze"]);
    }

    [Fact]
    public void Build_SelectedSeason_HasNoResultYet()
    {
        Input input = TwoEditions() with
        {
            Standings = [],
            Legs = [],
            Individuals = [],
            Seasons = new Dictionary<int, SeasonFacts>
            {
                [1] = new SeasonFacts(8, AnyRoundPlayed: false, IndividualComplete: false),
                [3] = new SeasonFacts(8, AnyRoundPlayed: true, IndividualComplete: false),
            },
        };

        CupTeamHistoryResponse response = Build(input);

        response.Seasons.Select(s => s.State).ShouldBe(["InProgress", "Selected"]);
        response.Seasons.ShouldAllBe(s => s.TeamRank == null && s.Medal == null && s.TeamScoreThousandths == null);
        response.Seasons.SelectMany(s => s.Squad).ShouldAllBe(m => m.Leg == null && m.Individual == null);
        response.Honours.BestRank.ShouldBeNull();
        response.Honours.BestRankSeasonNumber.ShouldBeNull();
        response.Roster.ShouldAllBe(r => r.BestGroupRank == null && r.TotalLegScoreThousandths == 0L);
    }

    [Fact]
    public void Build_TeamEventDoneButIndividualOpen_IsInProgress()
    {
        Input input = TwoEditions() with
        {
            Individuals = [],
            Seasons = new Dictionary<int, SeasonFacts>
            {
                [1] = new SeasonFacts(8, AnyRoundPlayed: true, IndividualComplete: false),
                [3] = new SeasonFacts(8, AnyRoundPlayed: true, IndividualComplete: true),
            },
        };

        Build(input).Seasons.Select(s => $"{s.SourceSeasonNumber}:{s.State}:{s.TeamRank}")
            .ShouldBe(["3:Completed:1", "1:InProgress:3"]);
    }

    [Fact]
    public void Build_EqualBestRank_ReportsTheLatestSeason()
    {
        Input input = TwoEditions() with
        {
            Standings =
            [
                new StandingRow(1, 2, 400_000, 380_000, 1, 5, "Silver"),
                new StandingRow(3, 2, 500_000, 470_000, 2, 6, "Silver"),
            ],
        };

        CupTeamHistoryResponse.TeamHonours honours = Build(input).Honours;
        honours.BestRank.ShouldBe(2);
        honours.BestRankSeasonNumber.ShouldBe(3);
        honours.Silver.ShouldBe(2);
    }

    [Fact]
    public void Build_NoSelections_IsNotFound()
    {
        Input input = TwoEditions() with { Selections = [] };
        Should.Throw<CupTeamHistoryNotFoundException>(() => Build(input));
    }

    [Fact]
    public void Build_WrongSquadSize_Aborts()
    {
        Input source = TwoEditions();
        Input input = source with { Selections = source.Selections.Where(s => !(s.SeasonNumber == 3 && s.AthleteId == 5)).ToList() };
        Should.Throw<InvalidOperationException>(() => Build(input)).Message.ShouldContain("Season 3");
    }

    [Fact]
    public void Build_LegOutsideSquad_Aborts()
    {
        Input source = TwoEditions();
        Input input = source with { Legs = [.. source.Legs, new LegRow(3, 4, 4, 2, 90_000, 85_000, 1)] };
        Should.Throw<InvalidOperationException>(() => Build(input)).Message.ShouldContain("athlete 4");
    }

    [Fact]
    public void Build_StandingWithoutSelection_Aborts()
    {
        Input source = TwoEditions();
        Input input = source with { Standings = [.. source.Standings, new StandingRow(5, 1, 1, 1, 0, 0, "Gold")] };
        Should.Throw<InvalidOperationException>(() => Build(input)).Message.ShouldContain("Season 5");
    }

    [Fact]
    public void Build_UnknownAthlete_Aborts()
    {
        Input source = TwoEditions();
        Input input = source with { Athletes = source.Athletes.Where(a => a.Key != 2).ToDictionary(a => a.Key, a => a.Value) };
        Should.Throw<InvalidOperationException>(() => Build(input)).Message.ShouldContain("athlete 2");
    }

    [Fact]
    public void Build_TypeTournamentFinalist_PreservesQualificationAndFinal()
    {
        Input input = TypeTournamentFinalist();

        CupTeamHistoryResponse response = Build(input);

        // One season number, two tournament stages: the Final leads so the
        // official result stays prominent, qualification follows.
        response.Seasons.Count.ShouldBe(2);
        response.Seasons.ShouldAllBe(s => s.SourceSeasonNumber == 2);
        CupTeamHistoryResponse.Season final = response.Seasons[0];
        final.TournamentPhase.ShouldBe(2);
        final.TournamentStage.ShouldBe("Final");
        final.TeamRank.ShouldBe(2);
        final.Medal.ShouldBe("Silver");
        final.QualifiedForFinal.ShouldBeTrue();
        final.EliminatedInQualification.ShouldBeFalse();
        CupTeamHistoryResponse.Season qual = response.Seasons[1];
        qual.TournamentPhase.ShouldBe(1);
        qual.QualificationGroup.ShouldBe(1);
        qual.TournamentStage.ShouldBe("Qualification Group A");
        qual.TeamRank.ShouldBe(5);
        qual.Medal.ShouldBe("None");
        qual.QualifiedForFinal.ShouldBeTrue();
        qual.EliminatedInQualification.ShouldBeFalse();

        // Stage-aware legs: the Final entry carries Final legs, the
        // qualification entry carries qualification legs, never merged.
        final.Squad.Select(m => m.Leg!.GroupRank).ShouldBe([1, 1, 2, 3]);
        final.Squad.Select(m => m.Leg!.GroupScoreThousandths).ShouldAllBe(v => v == 150_000);
        qual.Squad.Select(m => m.Leg!.GroupRank).ShouldBe([3, 4, 5, 6]);
        qual.Squad.Select(m => m.Leg!.GroupScoreThousandths).ShouldAllBe(v => v == 60_000);

        // Qualification victory is not an honour; only the Final silver counts.
        response.Honours.Editions.ShouldBe(1);
        response.Honours.Gold.ShouldBe(0);
        response.Honours.Silver.ShouldBe(1);
        response.Honours.Bronze.ShouldBe(0);
        response.Honours.BestRank.ShouldBe(2);
        response.Honours.BestRankSeasonNumber.ShouldBe(2);
    }

    [Fact]
    public void Build_TypeTournamentEliminated_ShowsQualificationCutoff()
    {
        Input source = TypeTournamentFinalist();
        StandingRow qualRow = source.Standings.Single(s => s.TournamentPhase == 1);
        StandingRow eliminated = qualRow with { QualifiedForFinal = false, EliminatedInQualification = true };
        Input input = source with
        {
            Standings = [eliminated],
            Legs = source.Legs.Where(l => l.TournamentPhase == 1).ToList(),
        };

        CupTeamHistoryResponse response = Build(input);

        CupTeamHistoryResponse.Season qualSeason = response.Seasons.Single();
        qualSeason.SourceSeasonNumber.ShouldBe(2);
        qualSeason.TournamentStage.ShouldBe("Qualification Group A");
        qualSeason.TeamRank.ShouldBe(5);
        qualSeason.EliminatedInQualification.ShouldBeTrue();
        qualSeason.QualifiedForFinal.ShouldBeFalse();
        response.Honours.Gold.ShouldBe(0);
        response.Honours.Silver.ShouldBe(0);
        response.Honours.Bronze.ShouldBe(0);
        response.Honours.BestRank.ShouldBeNull();
    }

    [Fact]
    public void Build_DuplicateStageStanding_Aborts()
    {
        Input source = TypeTournamentFinalist();
        StandingRow duplicate = source.Standings.First(s => s.TournamentPhase == 2);
        Input input = source with { Standings = [.. source.Standings, duplicate] };
        Should.Throw<InvalidOperationException>(() => Build(input)).Message.ShouldContain("duplicate");
    }

    [Fact]
    public void Build_LegWithoutStageStanding_Aborts()
    {
        Input source = TypeTournamentFinalist();
        Input input = source with
        {
            Standings = source.Standings.Where(s => s.TournamentPhase == 2).ToList(),
        };
        Should.Throw<InvalidOperationException>(() => Build(input)).Message.ShouldContain("without a matching tournament stage");
    }

    /// <summary>Goblin: Season 2 squad 11..14, 5th in Qualification Group A then 2nd in the Final.</summary>
    private static Input TypeTournamentFinalist()
    {
        static SelectionRow Pick(int athlete, int rank) =>
            new(2, athlete, rank, 900_000 - (rank * 10_000), 1000, 800, 600, 400, null);

        return new Input(
            Save,
            "Type",
            "Goblin",
            "Goblin",
            TeamSize: 4,
            Selections:
            [
                Pick(11, 1), Pick(12, 2), Pick(13, 3), Pick(14, 4),
            ],
            Standings:
            [
                new StandingRow(2, 5, 210_000, 190_000, 1, 3, "None", TournamentPhase: 1, QualificationGroup: 1, IsHonourEligible: false, QualifiedForFinal: true, EliminatedInQualification: false),
                new StandingRow(2, 2, 500_000, 470_000, 2, 6, "Silver", TournamentPhase: 2, QualificationGroup: 0, IsHonourEligible: true, QualifiedForFinal: true, EliminatedInQualification: false),
            ],
            Legs:
            [
                new LegRow(2, 11, 1, 3, 60_000, 55_000, 1, TournamentPhase: 1, QualificationGroup: 1),
                new LegRow(2, 12, 2, 4, 60_000, 55_000, 1, TournamentPhase: 1, QualificationGroup: 1),
                new LegRow(2, 13, 3, 5, 60_000, 55_000, 0, TournamentPhase: 1, QualificationGroup: 1),
                new LegRow(2, 14, 4, 6, 60_000, 55_000, 0, TournamentPhase: 1, QualificationGroup: 1),
                new LegRow(2, 11, 1, 1, 150_000, 140_000, 3, TournamentPhase: 2, QualificationGroup: 0),
                new LegRow(2, 12, 2, 1, 150_000, 140_000, 2, TournamentPhase: 2, QualificationGroup: 0),
                new LegRow(2, 13, 3, 2, 150_000, 140_000, 1, TournamentPhase: 2, QualificationGroup: 0),
                new LegRow(2, 14, 4, 3, 150_000, 140_000, 0, TournamentPhase: 2, QualificationGroup: 0),
            ],
            Individuals: [],
            Seasons: new Dictionary<int, SeasonFacts>
            {
                [2] = new SeasonFacts(40, AnyRoundPlayed: true, IndividualComplete: true),
            },
            Athletes: new Dictionary<int, SaveAthleteEntity>
            {
                [11] = new SaveAthleteEntity { Id = 11, Name = "GobA", ImageUrl = null },
                [12] = new SaveAthleteEntity { Id = 12, Name = "GobB", ImageUrl = null },
                [13] = new SaveAthleteEntity { Id = 13, Name = "GobC", ImageUrl = null },
                [14] = new SaveAthleteEntity { Id = 14, Name = "GobD", ImageUrl = null },
            });
    }

    /// <summary>Red: Season 1 squad 1,2,3,4 finished 3rd; Season 3 squad 1,2,3,5 won.</summary>
    private static Input TwoEditions()
    {
        static SelectionRow Pick(int season, int athlete, int rank) =>
            new(season, athlete, rank, 900_000 - (rank * 10_000), 1000, 800, 600, 400, null);

        return new Input(
            Save,
            "Color",
            "red",
            "Red",
            TeamSize: 4,
            Selections:
            [
                Pick(1, 1, 1), Pick(1, 2, 2), Pick(1, 3, 3), Pick(1, 4, 4),
                Pick(3, 1, 1), Pick(3, 2, 2), Pick(3, 3, 3), Pick(3, 5, 4),
            ],
            Standings:
            [
                new StandingRow(1, 3, 400_000, 380_000, 1, 5, "Bronze"),
                new StandingRow(3, 1, 500_000, 470_000, 2, 6, "Gold"),
            ],
            Legs:
            [
                new LegRow(1, 1, 1, 2, 100_000, 95_000, 2),
                new LegRow(1, 2, 2, 3, 100_000, 95_000, 1),
                new LegRow(1, 3, 3, 4, 100_000, 95_000, 1),
                new LegRow(1, 4, 4, 1, 100_000, 95_000, 1),
                new LegRow(3, 1, 1, 1, 150_000, 140_000, 3),
                new LegRow(3, 2, 2, 1, 150_000, 140_000, 2),
                new LegRow(3, 3, 3, 5, 100_000, 95_000, 1),
                new LegRow(3, 5, 4, 6, 100_000, 95_000, 0),
            ],
            Individuals:
            [
                new IndividualRow(1, 1, 9, 300_000, "None"),
                new IndividualRow(1, 2, 3, 350_000, "Bronze"),
                new IndividualRow(3, 1, 1, 420_000, "Gold"),
            ],
            Seasons: new Dictionary<int, SeasonFacts>
            {
                [1] = new SeasonFacts(8, AnyRoundPlayed: true, IndividualComplete: true),
                [3] = new SeasonFacts(8, AnyRoundPlayed: true, IndividualComplete: true),
            },
            Athletes: new Dictionary<int, SaveAthleteEntity>
            {
                [1] = new SaveAthleteEntity { Id = 1, Name = "Ash", ImageUrl = "https://img.test/1.jpg" },
                [2] = new SaveAthleteEntity { Id = 2, Name = "Birch", ImageUrl = "https://img.test/2.jpg" },
                [3] = new SaveAthleteEntity { Id = 3, Name = "Cedar", ImageUrl = "https://img.test/3.jpg" },
                [4] = new SaveAthleteEntity { Id = 4, Name = "Dogwood", ImageUrl = "https://img.test/4.jpg" },
                [5] = new SaveAthleteEntity { Id = 5, Name = "Elm", ImageUrl = null },
            });
    }
}
