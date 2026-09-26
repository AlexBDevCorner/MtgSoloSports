using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Scoring;

public sealed class ScoringTableBoundariesTests
{
    private static RulesV1 Rules => RulesV1.CreateDefault();

    public static TheoryData<int, int> ScoringTableData()
    {
        int[] expected =
        [
            77, 67, 58, 50, 43, 37, 32, 28, 25, 23, 22, 21, 20, 19, 18, 17,
            16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1,
        ];
        var data = new TheoryData<int, int>();
        for (int i = 0; i < expected.Length; i++)
        {
            data.Add(i + 1, expected[i]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScoringTableData))]
    public void BasePoints_CoverAllThirtyTwoPositions(int position, int expectedPoints)
    {
        ScoringCalculator.BaseRoundPointsForPosition(position, Rules)
            .Thousandths.ShouldBe(expectedPoints * 1000);
        ScoringCalculator.ChampionshipPointsForPosition(position, Rules)
            .Thousandths.ShouldBe(expectedPoints * 1000);
    }

    public static TheoryData<int, int> RoundBonusData()
    {
        int[] expected = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0, 0];
        var data = new TheoryData<int, int>();
        for (int i = 0; i < expected.Length; i++)
        {
            data.Add(i + 1, expected[i]);
        }

        // Spot-check the tail of the table: no bonus far outside the top ten.
        data.Add(16, 0);
        data.Add(32, 0);
        return data;
    }

    [Theory]
    [MemberData(nameof(RoundBonusData))]
    public void RoundBonus_RegularLeagueBoundary_IsExact(int position, int expectedThousandths)
    {
        ScoringCalculator.RoundBonusForPosition(position, Rules, isSuperleague: false)
            .Thousandths.ShouldBe(expectedThousandths);
    }

    public static TheoryData<int, int> StageBonusData()
    {
        int[] expected = [200, 180, 160, 140, 120, 100, 80, 60, 40, 20, 0, 0];
        var data = new TheoryData<int, int>();
        for (int i = 0; i < expected.Length; i++)
        {
            data.Add(i + 1, expected[i]);
        }

        data.Add(16, 0);
        data.Add(32, 0);
        return data;
    }

    [Theory]
    [MemberData(nameof(StageBonusData))]
    public void StageBonus_RegularLeagueBoundary_IsExact(int position, int expectedThousandths)
    {
        ScoringCalculator.StageBonusForPosition(position, Rules, isSuperleague: false)
            .Thousandths.ShouldBe(expectedThousandths);
    }

    [Theory]
    [InlineData(1, 100, 200)]
    [InlineData(2, 90, 180)]
    [InlineData(5, 60, 120)]
    [InlineData(10, 10, 20)]
    [InlineData(11, 0, 0)]
    [InlineData(32, 0, 0)]
    public void Superleague_DoublesBothBonusTables(int position, int expectedRound, int expectedStage)
    {
        ScoringCalculator.RoundBonusForPosition(position, Rules, isSuperleague: true)
            .Thousandths.ShouldBe(expectedRound * Rules.SuperleagueBonusMultiplier);
        ScoringCalculator.StageBonusForPosition(position, Rules, isSuperleague: true)
            .Thousandths.ShouldBe(expectedStage * Rules.SuperleagueBonusMultiplier);
    }

    [Theory]
    [InlineData(1, 100, 84700)]
    [InlineData(10, 10, 23230)]
    [InlineData(32, 0, 1000)]
    [InlineData(32, 10, 1010)]
    [InlineData(1, 1, 77077)]
    [InlineData(11, 370, 30140)]
    public void FinalRoundPoints_RetainFractionsExactly(int position, int bonusThousandths, int expectedThousandths)
    {
        ScoringCalculator.FinalRoundPointsForPosition(position, Bonus.FromThousandths(bonusThousandths), Rules)
            .Thousandths.ShouldBe(expectedThousandths);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(1, 160)]
    [InlineData(2, 120)]
    [InlineData(3, 80)]
    [InlineData(4, 40)]
    [InlineData(5, 0)]
    [InlineData(6, 0)]
    [InlineData(8, 0)]
    public void DecayAges_FollowLinearWeightsToZero(int age, int expected)
    {
        ScoringCalculator.ApplyDecay(Bonus.FromThousandths(200), age, Rules)
            .Thousandths.ShouldBe(expected);
    }
}
