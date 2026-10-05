using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using MtgSoloSports.SimulationKernel.Stages;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Scoring;

public sealed class TieredBonusTests
{
    private static RulesV2 Rules => RulesV2.CreateDefault();

    [Theory]
    [InlineData(1, 200, 100, 50, 25)]
    [InlineData(2, 180, 90, 45, 22)]
    [InlineData(5, 120, 60, 30, 15)]
    [InlineData(9, 40, 20, 10, 5)]
    [InlineData(10, 20, 10, 5, 2)]
    [InlineData(11, 0, 0, 0, 0)]
    [InlineData(32, 0, 0, 0, 0)]
    public void RoundBonus_EachTierIsExact(
        int position, int super, int f1, int f2, int f3)
    {
        ScoringCalculator.RoundBonusForPosition(position, Rules, LeagueLevel.Superleague).Thousandths.ShouldBe(super);
        ScoringCalculator.RoundBonusForPosition(position, Rules, LeagueLevel.Feeder1).Thousandths.ShouldBe(f1);
        ScoringCalculator.RoundBonusForPosition(position, Rules, LeagueLevel.Feeder2).Thousandths.ShouldBe(f2);
        ScoringCalculator.RoundBonusForPosition(position, Rules, LeagueLevel.Feeder3).Thousandths.ShouldBe(f3);
    }

    [Theory]
    [InlineData(1, 400, 200, 100, 50)]
    [InlineData(2, 360, 180, 90, 45)]
    [InlineData(5, 240, 120, 60, 30)]
    [InlineData(10, 40, 20, 10, 5)]
    [InlineData(11, 0, 0, 0, 0)]
    [InlineData(32, 0, 0, 0, 0)]
    public void StageBonus_EachTierIsExact(
        int position, int super, int f1, int f2, int f3)
    {
        ScoringCalculator.StageBonusForPosition(position, Rules, LeagueLevel.Superleague).Thousandths.ShouldBe(super);
        ScoringCalculator.StageBonusForPosition(position, Rules, LeagueLevel.Feeder1).Thousandths.ShouldBe(f1);
        ScoringCalculator.StageBonusForPosition(position, Rules, LeagueLevel.Feeder2).Thousandths.ShouldBe(f2);
        ScoringCalculator.StageBonusForPosition(position, Rules, LeagueLevel.Feeder3).Thousandths.ShouldBe(f3);
    }

    [Fact]
    public void TierRatios_MatchSportingIntent()
    {
        // Superleague 2x regular; F1 baseline; F2 half; F3 quarter.
        for (int position = 1; position <= 10; position++)
        {
            int f1 = ScoringCalculator.RoundBonusForPosition(position, Rules, LeagueLevel.Feeder1).Thousandths;
            ScoringCalculator.RoundBonusForPosition(position, Rules, LeagueLevel.Superleague).Thousandths.ShouldBe(checked(f1 * 2));
            ScoringCalculator.RoundBonusForPosition(position, Rules, LeagueLevel.Feeder2).Thousandths.ShouldBe(f1 / 2);
        }
    }

    [Theory]
    [InlineData(90, 1, 2, 45)]
    [InlineData(90, 1, 4, 22)]
    [InlineData(10, 1, 4, 2)]
    [InlineData(30, 1, 4, 7)]
    [InlineData(70, 1, 4, 17)]
    [InlineData(100, 2, 1, 200)]
    [InlineData(100, 1, 1, 100)]
    public void Truncation_MultipliesFirstThenDivides(int baseValue, int numerator, int denominator, int expected)
    {
        Bonus.FromThousandths(baseValue).ScaleRatio(numerator, denominator).Thousandths.ShouldBe(expected);
        new TierBonusScale(numerator, denominator).ScaleThousandths(baseValue).ShouldBe(expected);
    }

    [Fact]
    public void ExactlyRepresentableValues_StayExact()
    {
        ScoringCalculator.RoundBonusForPosition(1, Rules, LeagueLevel.Feeder2).Thousandths.ShouldBe(50);
        ScoringCalculator.RoundBonusForPosition(1, Rules, LeagueLevel.Feeder3).Thousandths.ShouldBe(25);
        ScoringCalculator.StageBonusForPosition(1, Rules, LeagueLevel.Feeder2).Thousandths.ShouldBe(100);
        ScoringCalculator.StageBonusForPosition(1, Rules, LeagueLevel.Feeder3).Thousandths.ShouldBe(50);
    }

    [Fact]
    public void ChampionshipPoints_AreIdenticalAcrossTiers()
    {
        for (int position = 1; position <= 32; position++)
        {
            int expected = Rules.ScoringTable[position - 1] * 1000;
            ScoringCalculator.ChampionshipPointsForPosition(position, Rules).Thousandths.ShouldBe(expected);
            ScoringCalculator.BaseRoundPointsForPosition(position, Rules).Thousandths.ShouldBe(expected);
        }
    }

    [Fact]
    public void StageRank_EarnedBonusScalesByTier_ChampionshipUnchanged()
    {
        IReadOnlyList<StageAthleteTotals> totals = StageCalculator.Accumulate(BuildUniformRounds(Rules), Rules);

        IReadOnlyList<StageRankedAthlete> super = StageCalculator.Rank(totals, new Pcg32V1(1UL, 2UL), Rules, LeagueLevel.Superleague);
        IReadOnlyList<StageRankedAthlete> f1 = StageCalculator.Rank(totals, new Pcg32V1(1UL, 2UL), Rules, LeagueLevel.Feeder1);
        IReadOnlyList<StageRankedAthlete> f2 = StageCalculator.Rank(totals, new Pcg32V1(1UL, 2UL), Rules, LeagueLevel.Feeder2);
        IReadOnlyList<StageRankedAthlete> f3 = StageCalculator.Rank(totals, new Pcg32V1(1UL, 2UL), Rules, LeagueLevel.Feeder3);

        // Winner: 16 round wins at base 100 plus stage win at base 200.
        f1.Single(r => r.StageRank == 1).EarnedBonusThousandths.ShouldBe((16 * 100) + 200);
        super.Single(r => r.StageRank == 1).EarnedBonusThousandths.ShouldBe(((16 * 100) + 200) * 2);
        f2.Single(r => r.StageRank == 1).EarnedBonusThousandths.ShouldBe(((16 * 100) + 200) / 2);
        f3.Single(r => r.StageRank == 1).EarnedBonusThousandths.ShouldBe(((16 * 100) + 200) / 4);

        foreach (StageRankedAthlete entry in super)
        {
            entry.ChampionshipPointsThousandths.ShouldBe(Rules.ScoringTable[entry.StageRank - 1] * 1000);
        }

        f1.Single(r => r.StageRank == 11).EarnedBonusThousandths.ShouldBe(0);
    }

    [Fact]
    public void HistoricalBonus_IsNeverRescaledByLaterMovement()
    {
        // Earned amounts retain what was actually earned at the original tier;
        // moving divisions only changes future earnings, never past rows.
        // A Superleague stage win (400) plus an F1 round win (100) plus an F2
        // round win (50) decay by season age without rescaling.
        var contributions = new[]
        {
            new BonusContribution(EarnedSeason: 1, EarnedStage: 1, Earned: Bonus.FromThousandths(400)),
            new BonusContribution(EarnedSeason: 1, EarnedStage: 2, Earned: Bonus.FromThousandths(100)),
            new BonusContribution(EarnedSeason: 2, EarnedStage: 1, Earned: Bonus.FromThousandths(50)),
        };

        // Current season 3, next stage 1: ages 2, 2, 1 → weights 600, 600, 800.
        Bonus effective = BonusCalculator.EffectiveBonus(contributions, currentSeason: 3, currentStage: 1, Rules);
        int expected = (400 * 600 / 1000) + (100 * 600 / 1000) + (50 * 800 / 1000);
        effective.Thousandths.ShouldBe(expected);
    }

    [Fact]
    public void BonusActivation_Stage32EntersNextSeasonAtEightyPercent()
    {
        var contributions = new[]
        {
            new BonusContribution(EarnedSeason: 1, EarnedStage: 32, Earned: Bonus.FromThousandths(200)),
        };

        Bonus nextSeason = BonusCalculator.EffectiveBonus(contributions, currentSeason: 2, currentStage: 1, Rules);
        nextSeason.Thousandths.ShouldBe(160);
    }

    [Fact]
    public void PoolResidence_ContinuesToAgeBonus()
    {
        var contributions = new[]
        {
            new BonusContribution(EarnedSeason: 1, EarnedStage: 1, Earned: Bonus.FromThousandths(200)),
        };

        BonusCalculator.EffectiveBonus(contributions, currentSeason: 1, currentStage: 2, Rules).Thousandths.ShouldBe(200);
        BonusCalculator.EffectiveBonus(contributions, currentSeason: 6, currentStage: 1, Rules).Thousandths.ShouldBe(0);
    }

    private static List<List<StageRoundEntry>> BuildUniformRounds(RulesV1 rules)
    {
        List<List<StageRoundEntry>> rounds = new(16);
        for (int round = 0; round < 16; round++)
        {
            List<StageRoundEntry> entries = new(32);
            for (int position = 1; position <= 32; position++)
            {
                int basePoints = rules.ScoringTable[position - 1] * 1000;
                entries.Add(new StageRoundEntry(position, $"Athlete {position:D2}", position, basePoints, basePoints));
            }

            rounds.Add(entries);
        }

        return rounds;
    }
}
