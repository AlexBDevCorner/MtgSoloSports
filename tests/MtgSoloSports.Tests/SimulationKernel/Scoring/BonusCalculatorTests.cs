using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Scoring;

public sealed class BonusCalculatorTests
{
    private static RulesV1 Rules => RulesV1.CreateDefault();

    [Fact]
    public void EffectiveBonus_EmptyContributions_ReturnsZero()
    {
        BonusCalculator.EffectiveBonus([], currentSeason: 1, currentStage: 1, Rules)
            .Thousandths.ShouldBe(0);
    }

    [Fact]
    public void EffectiveBonus_CurrentSeasonCompletedStages_CountAtFullWeight()
    {
        List<BonusContribution> contributions =
        [
            new(EarnedSeason: 2, EarnedStage: 1, Bonus.FromThousandths(100)),
            new(EarnedSeason: 2, EarnedStage: 2, Bonus.FromThousandths(90)),
            new(EarnedSeason: 2, EarnedStage: 3, Bonus.FromThousandths(200)),
        ];

        BonusCalculator.EffectiveBonus(contributions, currentSeason: 2, currentStage: 4, Rules)
            .Thousandths.ShouldBe(390);
    }

    [Fact]
    public void EffectiveBonus_PendingStagesAreExcluded()
    {
        List<BonusContribution> contributions =
        [
            new(EarnedSeason: 1, EarnedStage: 5, Bonus.FromThousandths(100)),
            new(EarnedSeason: 1, EarnedStage: 6, Bonus.FromThousandths(90)),
        ];

        // Stage 5 is completed, stage 6 is the current (pending) stage.
        BonusCalculator.EffectiveBonus(contributions, currentSeason: 1, currentStage: 6, Rules)
            .Thousandths.ShouldBe(100);
    }

    [Fact]
    public void EffectiveBonus_Stage32NeverActiveInSameSeason()
    {
        List<BonusContribution> contributions =
        [
            new(EarnedSeason: 3, EarnedStage: 32, Bonus.FromThousandths(200)),
        ];

        BonusCalculator.EffectiveBonus(contributions, currentSeason: 3, currentStage: 32, Rules)
            .Thousandths.ShouldBe(0);
    }

    [Fact]
    public void EffectiveBonus_Stage32EntersNextSeasonAtEightyPercent()
    {
        List<BonusContribution> contributions =
        [
            new(EarnedSeason: 1, EarnedStage: 32, Bonus.FromThousandths(200)),
        ];

        BonusCalculator.EffectiveBonus(contributions, currentSeason: 2, currentStage: 1, Rules)
            .Thousandths.ShouldBe(160);
    }

    [Fact]
    public void EffectiveBonus_PriorSeasonStagesDecayToEightyPercent()
    {
        List<BonusContribution> contributions =
        [
            new(EarnedSeason: 1, EarnedStage: 1, Bonus.FromThousandths(100)),
            new(EarnedSeason: 1, EarnedStage: 31, Bonus.FromThousandths(200)),
            new(EarnedSeason: 1, EarnedStage: 32, Bonus.FromThousandths(200)),
        ];

        // 100 * 0.8 + 200 * 0.8 + 200 * 0.8 = 400.
        BonusCalculator.EffectiveBonus(contributions, currentSeason: 2, currentStage: 1, Rules)
            .Thousandths.ShouldBe(400);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(1, 160)]
    [InlineData(2, 120)]
    [InlineData(3, 80)]
    [InlineData(4, 40)]
    [InlineData(5, 0)]
    [InlineData(6, 0)]
    [InlineData(10, 0)]
    public void EffectiveBonus_SeasonAgeFollowsLinearWeights(int age, int expected)
    {
        List<BonusContribution> contributions =
        [
            new(EarnedSeason: 1, EarnedStage: 7, Bonus.FromThousandths(200)),
        ];

        BonusCalculator.EffectiveBonus(contributions, currentSeason: 1 + age, currentStage: 8, Rules)
            .Thousandths.ShouldBe(expected);
    }

    [Fact]
    public void EffectiveBonus_AggregatesMultipleSeasonsWithDecay()
    {
        List<BonusContribution> contributions =
        [
            new(EarnedSeason: 4, EarnedStage: 10, Bonus.FromThousandths(1000)),
            new(EarnedSeason: 5, EarnedStage: 10, Bonus.FromThousandths(1000)),
            new(EarnedSeason: 6, EarnedStage: 5, Bonus.FromThousandths(1000)),
        ];

        // In season 6 stage 6: 1000 * 0.6 + 1000 * 0.8 + 1000 * 1.0 = 2400.
        BonusCalculator.EffectiveBonus(contributions, currentSeason: 6, currentStage: 6, Rules)
            .Thousandths.ShouldBe(2400);
    }

    [Fact]
    public void EffectiveBonus_SuperleagueDoubledEarningsDecayNormally()
    {
        // A Superleague stage win earns 400 (doubled 200); next season it is 320.
        List<BonusContribution> contributions =
        [
            new(EarnedSeason: 2, EarnedStage: 12, Bonus.FromThousandths(400)),
        ];

        BonusCalculator.EffectiveBonus(contributions, currentSeason: 3, currentStage: 1, Rules)
            .Thousandths.ShouldBe(320);
    }

    [Fact]
    public void EffectiveBonusFromSeasonTotals_AppliesDecayPerSeason()
    {
        List<SeasonBonus> totals =
        [
            new(Season: 1, Bonus.FromThousandths(1000)),
            new(Season: 2, Bonus.FromThousandths(1000)),
        ];

        // In season 3: 1000 * 0.6 + 1000 * 0.8 = 1400.
        BonusCalculator.EffectiveBonusFromSeasonTotals(totals, currentSeason: 3, Rules)
            .Thousandths.ShouldBe(1400);
    }

    [Fact]
    public void EffectiveBonusFromSeasonTotals_AgeFiveAndBeyondContributesZero()
    {
        List<SeasonBonus> totals = [new(Season: 1, Bonus.FromThousandths(5000))];

        BonusCalculator.EffectiveBonusFromSeasonTotals(totals, currentSeason: 6, Rules)
            .Thousandths.ShouldBe(0);
        BonusCalculator.EffectiveBonusFromSeasonTotals(totals, currentSeason: 9, Rules)
            .Thousandths.ShouldBe(0);
    }

    [Fact]
    public void EffectiveBonus_FutureContribution_Throws()
    {
        List<BonusContribution> contributions =
        [
            new(EarnedSeason: 5, EarnedStage: 1, Bonus.FromThousandths(100)),
        ];

        Should.Throw<InvalidOperationException>(() =>
            BonusCalculator.EffectiveBonus(contributions, currentSeason: 4, currentStage: 1, Rules));
    }

    [Fact]
    public void EffectiveBonusFromSeasonTotals_FutureSeason_Throws()
    {
        List<SeasonBonus> totals = [new(Season: 4, Bonus.FromThousandths(100))];

        Should.Throw<InvalidOperationException>(() =>
            BonusCalculator.EffectiveBonusFromSeasonTotals(totals, currentSeason: 3, Rules));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 33)]
    public void EffectiveBonus_InvalidCurrentPosition_Throws(int season, int stage)
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            BonusCalculator.EffectiveBonus([], season, stage, Rules));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 33)]
    public void EffectiveBonus_InvalidEarnedPosition_Throws(int earnedSeason, int earnedStage)
    {
        List<BonusContribution> contributions =
        [
            new(earnedSeason, earnedStage, Bonus.FromThousandths(10)),
        ];

        Should.Throw<ArgumentOutOfRangeException>(() =>
            BonusCalculator.EffectiveBonus(contributions, currentSeason: 4, currentStage: 4, Rules));
    }
}
