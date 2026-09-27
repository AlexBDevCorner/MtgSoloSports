using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// Proves the six-season bonus window is an exact optimization: contributions
/// older than five seasons decay to zero, so SQL pre-filtering reaches the
/// identical <see cref="BonusCalculator.EffectiveBonus"/> as full history.
/// </summary>
public sealed class BonusWindowTests
{
    [Fact]
    public void SelectBonusSeasonIds_KeepsSixSeasons()
    {
        Dictionary<int, int> seasonNumbers = new()
        {
            [10] = 1,
            [11] = 2,
            [12] = 3,
            [13] = 4,
            [14] = 5,
            [15] = 6,
            [16] = 7,
            [17] = 8,
        };
        IReadOnlySet<int> ids = AdvanceRoundHandler.SelectBonusSeasonIds(seasonNumbers, currentSeasonNumber: 8);
        ids.SetEquals([12, 13, 14, 15, 16, 17]).ShouldBeTrue();
    }

    [Fact]
    public void EffectiveBonus_IgnoresContributionsOlderThanFiveSeasons()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<BonusContribution> full =
        [
            new BonusContribution(1, 1, Bonus.FromThousandths(1000)),
            new BonusContribution(3, 1, Bonus.FromThousandths(500)),
        ];
        List<BonusContribution> windowed =
        [
            new BonusContribution(3, 1, Bonus.FromThousandths(500)),
        ];
        Bonus fromFull = BonusCalculator.EffectiveBonus(full, currentSeason: 9, currentStage: 1, rules);
        Bonus fromWindowed = BonusCalculator.EffectiveBonus(windowed, currentSeason: 9, currentStage: 1, rules);
        fromFull.Thousandths.ShouldBe(fromWindowed.Thousandths);
        fromFull.Thousandths.ShouldBe(
            ScoringCalculator.ApplyDecay(Bonus.FromThousandths(500), 6, rules).Thousandths);
    }

    [Fact]
    public void EffectiveBonus_Stage32EntersNextSeasonAt80Percent()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        BonusContribution stage32 = new(1, 32, Bonus.FromThousandths(1000));
        Bonus effective = BonusCalculator.EffectiveBonus([stage32], currentSeason: 2, currentStage: 1, rules);
        effective.Thousandths.ShouldBe(800);
    }
}
