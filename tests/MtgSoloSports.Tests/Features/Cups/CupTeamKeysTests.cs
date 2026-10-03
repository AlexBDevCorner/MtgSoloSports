using MtgSoloSports.Features.Cups.CupHistory;
using MtgSoloSports.SimulationKernel.Catalog;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

public sealed class CupTeamKeysTests
{
    [Fact]
    public void ColorKey_IsLowerCaseNameForEveryColor()
    {
        Enum.GetValues<SportingColor>().Select(CupTeamKeys.ColorKey)
            .ShouldBe(["white", "blue", "black", "red", "green", "multicolor", "hybrid", "colorless"]);
        CupTeamKeys.ColorName(SportingColor.Multicolor).ShouldBe("Multicolor");
    }

    [Theory]
    [InlineData("red", SportingColor.Red)]
    [InlineData("RED", SportingColor.Red)]
    [InlineData("Colorless", SportingColor.Colorless)]
    public void TryParseColorKey_AcceptsNamesIgnoringCase(string key, SportingColor expected)
    {
        CupTeamKeys.TryParseColorKey(key, out SportingColor color).ShouldBeTrue();
        color.ShouldBe(expected);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("purple")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(" red")]
    public void TryParseColorKey_RejectsNumbersAndUnknownNames(string? key)
    {
        CupTeamKeys.TryParseColorKey(key, out _).ShouldBeFalse();
    }

    [Fact]
    public void State_IsCompletedOnlyWhenEveryEventFinished()
    {
        CupEditionState.For(anyPlayed: false, complete: false).ShouldBe("Selected");
        CupEditionState.For(anyPlayed: true, complete: false).ShouldBe("InProgress");
        CupEditionState.For(anyPlayed: true, complete: true).ShouldBe("Completed");
    }
}
