using MtgSoloSports.SimulationKernel.FixedPoint;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Scoring;

public sealed class FixedPointTypesTests
{
    [Fact]
    public void Points_StoreThousandthsExactly()
    {
        Points.FromPoints(77).Thousandths.ShouldBe(77000);
        Points.FromThousandths(105490).WholePoints.ShouldBe(105);
        Points.FromThousandths(105490).RemainderThousandths.ShouldBe(490);
        (Points.FromPoints(10) + Points.FromPoints(5)).Thousandths.ShouldBe(15000);
    }

    [Fact]
    public void Bonus_ScalesByIntegerMultiplier()
    {
        Bonus.FromThousandths(100).Scale(2).Thousandths.ShouldBe(200);
        (Bonus.FromThousandths(100) + Bonus.FromThousandths(90)).Thousandths.ShouldBe(190);
    }

    [Fact]
    public void SelectionScore_CombinesWithIntegerWeights()
    {
        SelectionScore.Combine(1000, 1000, 1000, 1000, 350, 300, 250, 100).Thousandths.ShouldBe(1000);
        SelectionScore.Combine(1000, 0, 0, 0, 350, 300, 250, 100).Thousandths.ShouldBe(350);
    }

    [Fact]
    public void FixedPointTypes_RejectNegativeValues()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Points.FromThousandths(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => Bonus.FromThousandths(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => SelectionScore.FromThousandths(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => Bonus.FromThousandths(10).Scale(-1));
    }
}
