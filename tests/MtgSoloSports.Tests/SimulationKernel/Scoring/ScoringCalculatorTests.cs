using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Scoring;

public sealed class ScoringCalculatorTests
{
    private static RulesV1 Rules => RulesV1.CreateDefault();

    [Fact]
    public void ScoringTable_MatchesGameRulesV1()
    {
        int[] expected =
        [
            77, 67, 58, 50, 43, 37, 32, 28, 25, 23, 22, 21, 20, 19, 18, 17,
            16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1,
        ];

        Rules.ScoringTable.ShouldBe(expected);
    }

    [Fact]
    public void ApplyBonus_TechnicalDesignExample_IsExact()
    {
        Points basePoints = Points.FromPoints(77);
        Bonus bonus = Bonus.FromThousandths(370);

        Points finalPoints = ScoringCalculator.ApplyBonus(basePoints, bonus);

        finalPoints.Thousandths.ShouldBe(105490);
    }

    [Fact]
    public void FinalRoundPoints_PositionOne_WithTenthBonus()
    {
        Points finalPoints = ScoringCalculator.FinalRoundPointsForPosition(1, Bonus.FromThousandths(100), Rules);

        finalPoints.Thousandths.ShouldBe(84700);
    }

    [Fact]
    public void ChampionshipPoints_HaveNoBonusMultiplier()
    {
        ScoringCalculator.ChampionshipPointsForPosition(1, Rules).Thousandths.ShouldBe(77000);
        ScoringCalculator.ChampionshipPointsForPosition(32, Rules).Thousandths.ShouldBe(1000);
    }

    [Fact]
    public void RoundBonus_RegularAndSuperleague()
    {
        ScoringCalculator.RoundBonusForPosition(1, Rules, isSuperleague: false).Thousandths.ShouldBe(100);
        ScoringCalculator.RoundBonusForPosition(10, Rules, isSuperleague: false).Thousandths.ShouldBe(10);
        ScoringCalculator.RoundBonusForPosition(11, Rules, isSuperleague: false).Thousandths.ShouldBe(0);

        ScoringCalculator.RoundBonusForPosition(1, Rules, isSuperleague: true).Thousandths.ShouldBe(200);
        ScoringCalculator.RoundBonusForPosition(10, Rules, isSuperleague: true).Thousandths.ShouldBe(20);
        ScoringCalculator.RoundBonusForPosition(11, Rules, isSuperleague: true).Thousandths.ShouldBe(0);
    }

    [Fact]
    public void StageBonus_RegularAndSuperleague()
    {
        ScoringCalculator.StageBonusForPosition(1, Rules, isSuperleague: false).Thousandths.ShouldBe(200);
        ScoringCalculator.StageBonusForPosition(10, Rules, isSuperleague: false).Thousandths.ShouldBe(20);
        ScoringCalculator.StageBonusForPosition(11, Rules, isSuperleague: false).Thousandths.ShouldBe(0);

        ScoringCalculator.StageBonusForPosition(1, Rules, isSuperleague: true).Thousandths.ShouldBe(400);
        ScoringCalculator.StageBonusForPosition(5, Rules, isSuperleague: true).Thousandths.ShouldBe(240);
    }

    [Fact]
    public void StageTotal_SumsSixteenRounds()
    {
        RulesV1 rules = Rules;
        List<Points> rounds = [];
        for (int i = 0; i < rules.RoundsPerStage; i++)
        {
            rounds.Add(Points.FromPoints(10));
        }

        ScoringCalculator.StageTotal(rounds).Thousandths.ShouldBe(160000);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(1, 160)]
    [InlineData(2, 120)]
    [InlineData(3, 80)]
    [InlineData(4, 40)]
    [InlineData(5, 0)]
    [InlineData(6, 0)]
    [InlineData(9, 0)]
    public void ApplyDecay_FollowsLinearV1Weights(int age, int expected)
    {
        ScoringCalculator.ApplyDecay(Bonus.FromThousandths(200), age, Rules).Thousandths.ShouldBe(expected);
    }

    [Fact]
    public void SelectionRating_UsesIntegerCupWeights()
    {
        SelectionScore full = ScoringCalculator.SelectionRating(1000, 1000, 1000, 1000, Rules);
        full.Thousandths.ShouldBe(1000);

        SelectionScore bonusOnly = ScoringCalculator.SelectionRating(1000, 0, 0, 0, Rules);
        bonusOnly.Thousandths.ShouldBe(350);

        SelectionScore mixed = ScoringCalculator.SelectionRating(1000, 500, 200, 0, Rules);
        mixed.Thousandths.ShouldBe(350 + 150 + 50);
    }

    [Fact]
    public void Positions_OutsideLeagueSize_AreRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => ScoringCalculator.ChampionshipPointsForPosition(0, Rules));
        Should.Throw<ArgumentOutOfRangeException>(() => ScoringCalculator.ChampionshipPointsForPosition(33, Rules));
        Should.Throw<ArgumentOutOfRangeException>(() => ScoringCalculator.RoundBonusForPosition(33, Rules, isSuperleague: false));
    }
}
