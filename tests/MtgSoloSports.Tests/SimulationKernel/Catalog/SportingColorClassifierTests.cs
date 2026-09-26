using MtgSoloSports.SimulationKernel.Catalog;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Catalog;

public sealed class SportingColorClassifierTests
{
    [Theory]
    [InlineData("W", SportingColor.White)]
    [InlineData("U", SportingColor.Blue)]
    [InlineData("B", SportingColor.Black)]
    [InlineData("R", SportingColor.Red)]
    [InlineData("G", SportingColor.Green)]
    public void MonoColor_MapsToMatchingLeague(string color, SportingColor expected)
    {
        SportingColorClassifier.Classify([color], hasHybridMana: false, hasDevoid: false).ShouldBe(expected);
    }

    [Fact]
    public void TwoOrMoreColors_MapsToMulticolor()
    {
        SportingColorClassifier.Classify(["W", "U"], hasHybridMana: false, hasDevoid: false).ShouldBe(SportingColor.Multicolor);
        SportingColorClassifier.Classify(["W", "U", "B", "R", "G"], hasHybridMana: false, hasDevoid: false).ShouldBe(SportingColor.Multicolor);
    }

    [Fact]
    public void HybridMana_TakesPrecedenceOverMulticolor()
    {
        SportingColorClassifier.Classify(["W", "U"], hasHybridMana: true, hasDevoid: false).ShouldBe(SportingColor.Hybrid);
        SportingColorClassifier.Classify(["G"], hasHybridMana: true, hasDevoid: false).ShouldBe(SportingColor.Hybrid);
        SportingColorClassifier.Classify([], hasHybridMana: true, hasDevoid: false).ShouldBe(SportingColor.Hybrid);
    }

    [Fact]
    public void Devoid_MapsToColorless()
    {
        SportingColorClassifier.Classify(["U"], hasHybridMana: false, hasDevoid: true).ShouldBe(SportingColor.Colorless);
        SportingColorClassifier.Classify(["W", "U"], hasHybridMana: false, hasDevoid: true).ShouldBe(SportingColor.Colorless);
        SportingColorClassifier.Classify(["W", "U"], hasHybridMana: true, hasDevoid: true).ShouldBe(SportingColor.Colorless);
        SportingColorClassifier.Classify([], hasHybridMana: false, hasDevoid: true).ShouldBe(SportingColor.Colorless);
    }

    [Fact]
    public void NoColors_MapsToColorless()
    {
        SportingColorClassifier.Classify([], hasHybridMana: false, hasDevoid: false).ShouldBe(SportingColor.Colorless);
    }

    [Fact]
    public void DuplicateColors_AreIgnored()
    {
        SportingColorClassifier.Classify(["W", "W"], hasHybridMana: false, hasDevoid: false).ShouldBe(SportingColor.White);
    }

    [Fact]
    public void LowercaseColors_AreNormalized()
    {
        SportingColorClassifier.Classify(["w"], hasHybridMana: false, hasDevoid: false).ShouldBe(SportingColor.White);
    }

    [Fact]
    public void UnknownColor_Throws()
    {
        Should.Throw<InvalidOperationException>(() => SportingColorClassifier.Classify(["X"], hasHybridMana: false, hasDevoid: false));
    }

    [Fact]
    public void EmptyColorEntry_Throws()
    {
        Should.Throw<InvalidOperationException>(() => SportingColorClassifier.Classify([" "], hasHybridMana: false, hasDevoid: false));
    }

    [Fact]
    public void NormalizeColors_SortsWubrgOrder()
    {
        SportingColorClassifier.NormalizeColors(["G", "W"]).ShouldBe(["W", "G"]);
        SportingColorClassifier.NormalizeColors([]).ShouldBeEmpty();
    }
}
