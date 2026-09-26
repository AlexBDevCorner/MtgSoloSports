using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Rules;

public sealed class RulesV1Tests
{
    [Fact]
    public void CreateDefault_IsInternallyConsistent()
    {
        RulesV1 rules = RulesV1.CreateDefault();

        Should.NotThrow(() => rules.Validate());
        rules.Version.ShouldBe(1);
        rules.Algorithm.ShouldBe("Pcg32V1");
        rules.AlgorithmVersion.ShouldBe(1);
    }

    [Fact]
    public void CreateDefault_CoversCompleteV1Constants()
    {
        RulesV1 rules = RulesV1.CreateDefault();

        rules.TotalAthletesInSave.ShouldBe(2048);
        rules.AthletesPerSportingColor.ShouldBe(256);
        rules.SportingColorCount.ShouldBe(8);
        rules.RegularLeagueCount.ShouldBe(8);
        rules.LeagueSize.ShouldBe(32);
        rules.SuperleagueSize.ShouldBe(32);
        rules.StagesPerSeason.ShouldBe(32);
        rules.RoundsPerStage.ShouldBe(16);

        rules.RoundBonusThousandths.ShouldBe([100, 90, 80, 70, 60, 50, 40, 30, 20, 10]);
        rules.StageBonusThousandths.ShouldBe([200, 180, 160, 140, 120, 100, 80, 60, 40, 20]);
        rules.SuperleagueBonusMultiplier.ShouldBe(2);
        rules.BonusAgeWeightsThousandths.ShouldBe([1000, 800, 600, 400, 200, 0]);

        rules.QualifierSize.ShouldBe(32);
        rules.QualifierRounds.ShouldBe(16);
        rules.QualifierWinners.ShouldBe(8);
        rules.SuperleagueSafeCount.ShouldBe(16);
        rules.SuperleagueRelegatedCount.ShouldBe(8);
        rules.SuperleagueQualifierIncumbentCount.ShouldBe(8);
        rules.FeederAutoPromotedCount.ShouldBe(8);
        rules.FeederQualifierCount.ShouldBe(24);
        rules.InauguralQualifiedPerLeague.ShouldBe(4);

        rules.ColorCupColorCount.ShouldBe(8);
        rules.ColorCupTeamSize.ShouldBe(4);
        rules.ColorCupIndividualRounds.ShouldBe(16);
        rules.ColorCupTeamGroupRounds.ShouldBe(8);
        rules.TypeCupMinTeamSize.ShouldBe(4);
        rules.TypeCupGroupRounds.ShouldBe(8);
        rules.RecentFormStageCount.ShouldBe(10);
        rules.RecentFormWeights.ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        rules.CupBonusWeightPermille.ShouldBe(350);
        rules.CupPerformanceWeightPermille.ShouldBe(300);
        rules.CupFormWeightPermille.ShouldBe(250);
        rules.CupPrestigeWeightPermille.ShouldBe(100);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void InvalidLeagueSize_FailsValidation(int leagueSize)
    {
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(new RulesV1Overrides { LeagueSize = leagueSize }));
    }

    [Fact]
    public void InvalidScoringTable_FailsValidation()
    {
        int[] broken = [.. RulesV1.CreateDefault().ScoringTable];
        broken[0] = 78;

        Should.Throw<InvalidOperationException>(() => RulesV1.Create(new RulesV1Overrides { ScoringTable = broken }));
    }

    [Fact]
    public void ShortScoringTable_FailsValidation()
    {
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(new RulesV1Overrides { ScoringTable = [10, 9, 8] }));
    }

    [Fact]
    public void InvalidRoundBonus_FailsValidation()
    {
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(new RulesV1Overrides
        {
            RoundBonusThousandths = [100, 90, 80, 70, 60, 50, 40, 30, 20, 11],
        }));
    }

    [Fact]
    public void InvalidDecayWeights_FailsValidation()
    {
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(new RulesV1Overrides
        {
            BonusAgeWeightsThousandths = [1000, 800, 600, 400, 200, 100],
        }));
    }

    [Fact]
    public void InvalidCupWeights_FailsValidation()
    {
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(new RulesV1Overrides { CupBonusWeightPermille = 400 }));
    }

    [Fact]
    public void InvalidMovementCounts_FailValidation()
    {
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(new RulesV1Overrides { SuperleagueSafeCount = 15 }));
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(new RulesV1Overrides { QualifierSize = 30 }));
    }

    [Fact]
    public void InvalidSaveTotals_FailValidation()
    {
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(new RulesV1Overrides { TotalAthletesInSave = 2000 }));
    }

    [Fact]
    public void Snapshot_IsImmutableFromSimulationCode()
    {
        RulesV1 rules = RulesV1.CreateDefault();

        IList<int> scoring = (IList<int>)rules.ScoringTable;
        scoring.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => scoring[0] = 999);
        rules.ScoringTable[0].ShouldBe(77);

        IList<int> roundBonus = (IList<int>)rules.RoundBonusThousandths;
        roundBonus.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => roundBonus[0] = 999);
        rules.RoundBonusThousandths[0].ShouldBe(100);
    }

    [Fact]
    public void Create_CopiesInputTables()
    {
        int[] input = [.. RulesV1.CreateDefault().ScoringTable];

        RulesV1 rules = RulesV1.Create(new RulesV1Overrides { ScoringTable = input });
        input[0] = 1;

        rules.ScoringTable[0].ShouldBe(77);
    }
}
