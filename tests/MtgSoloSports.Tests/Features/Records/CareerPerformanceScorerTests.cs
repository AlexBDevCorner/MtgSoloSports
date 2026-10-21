using MtgSoloSports.Features.Records.GetHallOfFame;
using MtgSoloSports.SimulationKernel.Leagues;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Records;

public sealed class CareerPerformanceScorerTests
{
    [Fact]
    public void PlacementFactor_MonotonicFieldNormalized_WithPositiveFloor()
    {
        int first = CareerPerformanceScorer.PlacementFactorThousandths(1, 32);
        int fifthSmall = CareerPerformanceScorer.PlacementFactorThousandths(5, 16);
        int fifthLarge = CareerPerformanceScorer.PlacementFactorThousandths(5, 64);
        int last = CareerPerformanceScorer.PlacementFactorThousandths(32, 32);
        int p28 = CareerPerformanceScorer.PlacementFactorThousandths(28, 32);

        first.ShouldBe(1000);
        fifthLarge.ShouldBeGreaterThan(fifthSmall);
        p28.ShouldBeGreaterThan(last);
        last.ShouldBeGreaterThan(0);
        last.ShouldBeLessThan(first);
    }

    [Fact]
    public void PlacementFactor_OneAthleteField_ScoresFull()
    {
        CareerPerformanceScorer.PlacementFactorThousandths(1, 1).ShouldBe(1000);
        int points = CareerPerformanceScorer.ScoreEvent(new CareerPerformanceScorer.PlacementEvent(
            1, 1, CareerPerformanceScorer.Category.Feeder3, 1, 1, IsFinal: true));
        points.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void PlacementFactor_InvalidRankOrField_Aborts()
    {
        Should.Throw<InvalidOperationException>(() => CareerPerformanceScorer.PlacementFactorThousandths(0, 32));
        Should.Throw<InvalidOperationException>(() => CareerPerformanceScorer.PlacementFactorThousandths(33, 32));
        Should.Throw<InvalidOperationException>(() => CareerPerformanceScorer.PlacementFactorThousandths(1, 0));
    }

    [Fact]
    public void ScoreEvent_BetterRankAlwaysScoresMore()
    {
        int first = ScoreOf(CareerPerformanceScorer.Category.Superleague, 1, 32);
        int second = ScoreOf(CareerPerformanceScorer.Category.Superleague, 2, 32);
        int mid = ScoreOf(CareerPerformanceScorer.Category.Superleague, 16, 32);
        int last = ScoreOf(CareerPerformanceScorer.Category.Superleague, 32, 32);
        first.ShouldBeGreaterThan(second);
        second.ShouldBeGreaterThan(mid);
        mid.ShouldBeGreaterThan(last);
        last.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Weights_ThreeSuperleagueTitlesBeatFourFeeder1Titles()
    {
        int superTitle = ScoreOf(CareerPerformanceScorer.Category.Superleague, 1, 32);
        int feeder1Title = ScoreOf(CareerPerformanceScorer.Category.Feeder1, 1, 32);
        superTitle.ShouldBe(1500);
        feeder1Title.ShouldBe(600);
        checked(3 * superTitle).ShouldBeGreaterThan(checked(4 * feeder1Title));
    }

    [Fact]
    public void Weights_StrongNonWinnerBeatsLowerTierAccumulation()
    {
        int superRunnerUp = ScoreOf(CareerPerformanceScorer.Category.Superleague, 2, 32);
        int feeder1Title = ScoreOf(CareerPerformanceScorer.Category.Feeder1, 1, 32);
        checked(2 * superRunnerUp).ShouldBeGreaterThan(checked(4 * feeder1Title));
    }

    [Fact]
    public void Weights_MajorCupsMatter_TeamShareBelowIndividual()
    {
        int individualTitle = ScoreOf(CareerPerformanceScorer.Category.ColorCupIndividual, 1, 32);
        int teamTitle = ScoreOf(CareerPerformanceScorer.Category.ColorCupTeam, 1, 8);
        int feeder1Title = ScoreOf(CareerPerformanceScorer.Category.Feeder1, 1, 32);
        individualTitle.ShouldBeGreaterThan(feeder1Title);
        teamTitle.ShouldBeGreaterThan(0);
        teamTitle.ShouldBeLessThan(individualTitle);
        teamTitle.ShouldBeLessThan(feeder1Title);
    }

    [Fact]
    public void Weights_TierOrder_SuperAboveFeeder1AboveFeeder2AboveFeeder3()
    {
        ScoreOf(CareerPerformanceScorer.Category.Superleague, 1, 32)
            .ShouldBeGreaterThan(ScoreOf(CareerPerformanceScorer.Category.Feeder1, 1, 32));
        ScoreOf(CareerPerformanceScorer.Category.Feeder1, 5, 32)
            .ShouldBeGreaterThan(ScoreOf(CareerPerformanceScorer.Category.Feeder2, 5, 32));
        ScoreOf(CareerPerformanceScorer.Category.Feeder2, 5, 32)
            .ShouldBeGreaterThan(ScoreOf(CareerPerformanceScorer.Category.Feeder3, 5, 32));
    }

    [Fact]
    public void ComputeScores_DeterministicTieBreakInputs()
    {
        var events = new List<CareerPerformanceScorer.PlacementEvent>
        {
            new(1, 1, CareerPerformanceScorer.Category.Superleague, 1, 32, true),
            new(2, 1, CareerPerformanceScorer.Category.Superleague, 1, 32, true),
        };
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> scores =
            CareerPerformanceScorer.ComputeScores(events);
        scores[1].TotalPoints.ShouldBe(scores[2].TotalPoints);
        List<HallOfFameEntry> ranked = GetHallOfFameHandler.RankEntries(
            [
                new HallOfFameEntry(0, 2, "Beta", 0, "White", 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    scores[2].TotalPoints, 1500, 0, 0, 0, 0, 0, 0, 1, 1),
                new HallOfFameEntry(0, 1, "Alpha", 0, "White", 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    scores[1].TotalPoints, 1500, 0, 0, 0, 0, 0, 0, 1, 1),
            ], 10);
        ranked[0].AthleteId.ShouldBe(1);
        ranked[0].Rank.ShouldBe(1);
        ranked[1].Rank.ShouldBe(2);
    }

    [Fact]
    public void CategoryForLevel_MapsAllTiers()
    {
        CareerPerformanceScorer.CategoryForLevel(LeagueLevel.Superleague)
            .ShouldBe(CareerPerformanceScorer.Category.Superleague);
        CareerPerformanceScorer.CategoryForLevel(LeagueLevel.Feeder1)
            .ShouldBe(CareerPerformanceScorer.Category.Feeder1);
        CareerPerformanceScorer.CategoryForLevel(LeagueLevel.Feeder2)
            .ShouldBe(CareerPerformanceScorer.Category.Feeder2);
        CareerPerformanceScorer.CategoryForLevel(LeagueLevel.Feeder3)
            .ShouldBe(CareerPerformanceScorer.Category.Feeder3);
    }

    private static int ScoreOf(CareerPerformanceScorer.Category category, int rank, int fieldSize) =>
        CareerPerformanceScorer.ScoreEvent(new CareerPerformanceScorer.PlacementEvent(7, 3, category, rank, fieldSize, IsFinal: true));
}
