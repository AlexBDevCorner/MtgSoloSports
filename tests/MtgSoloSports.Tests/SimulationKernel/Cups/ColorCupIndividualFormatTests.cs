using MtgSoloSports.SimulationKernel.Cups;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

/// <summary>
/// MSS-119: pure individual Color Cup format mathematics. Proves the legacy
/// 16x32 versus phased 16x32+16x16 discriminator, legal phase/roster sizes,
/// stage completion and per-athlete participation counts.
/// </summary>
public sealed class ColorCupIndividualFormatTests
{
    [Fact]
    public void Format_DefinesLegacyAndPhasedVersions()
    {
        ColorCupIndividualFormat.LegacyFormatVersion.ShouldBe(0);
        ColorCupIndividualFormat.PhasedFormatVersion.ShouldBe(1);
        ColorCupIndividualFormat.QualifyingRounds.ShouldBe(16);
        ColorCupIndividualFormat.FinalRounds.ShouldBe(16);
        ColorCupIndividualFormat.QualifyingFieldSize.ShouldBe(32);
        ColorCupIndividualFormat.FinalFieldSize.ShouldBe(16);
        ColorCupIndividualFormat.CutoffRank.ShouldBe(16);
        ColorCupIndividualFormat.FieldSize.ShouldBe(32);
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 32)]
    public void TotalRounds_MatchesFormat(int formatVersion, int expected)
    {
        ColorCupIndividualFormat.TotalRounds(formatVersion).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, 0, 32)]
    [InlineData(16, 0, 32)]
    [InlineData(1, 1, 32)]
    [InlineData(16, 1, 32)]
    [InlineData(17, 1, 16)]
    [InlineData(32, 1, 16)]
    public void ExpectedFieldSize_MatchesPhase(int roundNumber, int formatVersion, int expected)
    {
        ColorCupIndividualFormat.ExpectedFieldSize(roundNumber, formatVersion).ShouldBe(expected);
    }

    [Fact]
    public void LegacyFormat_RejectsRound17()
    {
        Should.Throw<InvalidOperationException>(() => ColorCupIndividualFormat.ExpectedFieldSize(17, 0));
        Should.Throw<InvalidOperationException>(() => ColorCupIndividualFormat.ValidateRoundNumber(17, 0));
    }

    [Fact]
    public void PhasedFormat_RejectsRound33()
    {
        Should.Throw<InvalidOperationException>(() => ColorCupIndividualFormat.ExpectedFieldSize(33, 1));
        Should.Throw<InvalidOperationException>(() => ColorCupIndividualFormat.ValidateRoundNumber(33, 1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void UnknownFormatVersion_Rejected(int formatVersion)
    {
        Should.Throw<InvalidOperationException>(() => ColorCupIndividualFormat.TotalRounds(formatVersion));
        Should.Throw<InvalidOperationException>(() => ColorCupIndividualFormat.ValidateFormatVersion(formatVersion));
    }

    [Theory]
    [InlineData(1, 0, 16)]
    [InlineData(32, 0, 16)]
    [InlineData(1, 1, 32)]
    [InlineData(16, 1, 32)]
    [InlineData(17, 1, 16)]
    [InlineData(32, 1, 16)]
    public void ExpectedAppearances_MatchesParticipation(int cupRank, int formatVersion, int expected)
    {
        ColorCupIndividualFormat.ExpectedAppearancesForRank(cupRank, formatVersion).ShouldBe(expected);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(16, 1, true)]
    [InlineData(17, 1, false)]
    [InlineData(32, 1, false)]
    public void QualifiesForFinal_ClassifiesCutoff(int round16Rank, int formatVersion, bool expected)
    {
        ColorCupIndividualFormat.QualifiesForFinal(round16Rank, formatVersion).ShouldBe(expected);
    }

    [Theory]
    [InlineData(16, 0, true)]
    [InlineData(15, 0, false)]
    [InlineData(32, 1, true)]
    [InlineData(31, 1, false)]
    public void IsComplete_MatchesStageCompletion(int playedCount, int formatVersion, bool expected)
    {
        ColorCupIndividualFormat.IsComplete(playedCount, formatVersion).ShouldBe(expected);
    }

    [Fact]
    public void ResolveStored_MissingReadsAsLegacy()
    {
        ColorCupIndividualFormat.ResolveStored(null).ShouldBe(0);
        ColorCupIndividualFormat.ResolveStored(0).ShouldBe(0);
        ColorCupIndividualFormat.ResolveStored(1).ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => ColorCupIndividualFormat.ResolveStored(2));
    }

    [Fact]
    public void RoundsPerStage_DecoupledFromLeagueStageCount()
    {
        // MSS-119: qualifying rounds stay 16 without requiring equality with
        // the league RoundsPerStage. Both are 16 today; the rule no longer
        // ties them together.
        var rules = MtgSoloSports.SimulationKernel.Rules.RulesV1.CreateDefault();
        rules.ColorCupIndividualRounds.ShouldBe(16);
        rules.RoundsPerStage.ShouldBe(16);
        rules.ColorCupIndividualRounds.ShouldBe(ColorCupIndividualFormat.QualifyingRounds);
    }
}
