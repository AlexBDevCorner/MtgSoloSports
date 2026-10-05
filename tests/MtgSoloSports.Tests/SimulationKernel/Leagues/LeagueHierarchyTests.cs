using MtgSoloSports.SimulationKernel.Leagues;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Leagues;

public sealed class LeagueHierarchyTests
{
    [Fact]
    public void Levels_CoverFourTiersDistinctly()
    {
        LeagueHierarchy.AllLevels.ShouldBe(
            [LeagueLevel.Superleague, LeagueLevel.Feeder1, LeagueLevel.Feeder2, LeagueLevel.Feeder3]);
        LeagueHierarchy.FeederLevels.ShouldBe(
            [LeagueLevel.Feeder1, LeagueLevel.Feeder2, LeagueLevel.Feeder3]);
    }

    [Fact]
    public void IsFeeder_DistinguishesKind()
    {
        LeagueHierarchy.IsSuperleague(LeagueLevel.Superleague).ShouldBeTrue();
        LeagueHierarchy.IsFeeder(LeagueLevel.Superleague).ShouldBeFalse();

        foreach (LeagueLevel feeder in LeagueHierarchy.FeederLevels)
        {
            LeagueHierarchy.IsFeeder(feeder).ShouldBeTrue();
            LeagueHierarchy.IsSuperleague(feeder).ShouldBeFalse();
        }
    }

    [Fact]
    public void FeederDivision_MapsExplicitly()
    {
        LeagueHierarchy.FeederDivisionFor(LeagueLevel.Superleague).ShouldBe(FeederDivision.None);
        LeagueHierarchy.FeederDivisionFor(LeagueLevel.Feeder1).ShouldBe(FeederDivision.First);
        LeagueHierarchy.FeederDivisionFor(LeagueLevel.Feeder2).ShouldBe(FeederDivision.Second);
        LeagueHierarchy.FeederDivisionFor(LeagueLevel.Feeder3).ShouldBe(FeederDivision.Third);
    }

    [Fact]
    public void LevelForDivision_RoundTrips()
    {
        LeagueHierarchy.LevelForDivision(FeederDivision.None, isSuperleague: true).ShouldBe(LeagueLevel.Superleague);
        LeagueHierarchy.LevelForDivision(FeederDivision.First, isSuperleague: false).ShouldBe(LeagueLevel.Feeder1);
        LeagueHierarchy.LevelForDivision(FeederDivision.Second, isSuperleague: false).ShouldBe(LeagueLevel.Feeder2);
        LeagueHierarchy.LevelForDivision(FeederDivision.Third, isSuperleague: false).ShouldBe(LeagueLevel.Feeder3);
    }

    [Fact]
    public void LevelForDivision_HistoricalV1FeederMapsToFeeder1()
    {
        // v1 rows predate the division column (stored 0/None) and represent the
        // single historical feeder tier, surfaced as Feeder 1.
        LeagueHierarchy.LevelForDivision(FeederDivision.None, isSuperleague: false).ShouldBe(LeagueLevel.Feeder1);
    }

    [Fact]
    public void DisplayName_DoesNotRequireNameParsing()
    {
        LeagueHierarchy.DisplayName(LeagueLevel.Superleague).ShouldBe("Superleague");
        LeagueHierarchy.DisplayName(LeagueLevel.Feeder1).ShouldBe("Feeder 1");
        LeagueHierarchy.DisplayName(LeagueLevel.Feeder2).ShouldBe("Feeder 2");
        LeagueHierarchy.DisplayName(LeagueLevel.Feeder3).ShouldBe("Feeder 3");
    }

    [Fact]
    public void AdjacentTiers_WalkPyramid()
    {
        LeagueHierarchy.HigherTier(LeagueLevel.Superleague).ShouldBeNull();
        LeagueHierarchy.HigherTier(LeagueLevel.Feeder1).ShouldBe(LeagueLevel.Superleague);
        LeagueHierarchy.HigherTier(LeagueLevel.Feeder2).ShouldBe(LeagueLevel.Feeder1);
        LeagueHierarchy.HigherTier(LeagueLevel.Feeder3).ShouldBe(LeagueLevel.Feeder2);

        LeagueHierarchy.LowerTier(LeagueLevel.Superleague).ShouldBe(LeagueLevel.Feeder1);
        LeagueHierarchy.LowerTier(LeagueLevel.Feeder1).ShouldBe(LeagueLevel.Feeder2);
        LeagueHierarchy.LowerTier(LeagueLevel.Feeder2).ShouldBe(LeagueLevel.Feeder3);
        LeagueHierarchy.LowerTier(LeagueLevel.Feeder3).ShouldBeNull();
    }

    [Fact]
    public void Order_RanksSuperleagueFirst()
    {
        LeagueHierarchy.Order(LeagueLevel.Superleague).ShouldBe(0);
        LeagueHierarchy.Order(LeagueLevel.Feeder1).ShouldBe(1);
        LeagueHierarchy.Order(LeagueLevel.Feeder2).ShouldBe(2);
        LeagueHierarchy.Order(LeagueLevel.Feeder3).ShouldBe(3);
    }

    [Fact]
    public void DefaultBonusScale_IsExactRational()
    {
        LeagueHierarchy.DefaultBonusScale(LeagueLevel.Superleague).ShouldBe(new TierBonusScale(2, 1));
        LeagueHierarchy.DefaultBonusScale(LeagueLevel.Feeder1).ShouldBe(new TierBonusScale(1, 1));
        LeagueHierarchy.DefaultBonusScale(LeagueLevel.Feeder2).ShouldBe(new TierBonusScale(1, 2));
        LeagueHierarchy.DefaultBonusScale(LeagueLevel.Feeder3).ShouldBe(new TierBonusScale(1, 4));
    }

    [Fact]
    public void LegacyFlag_MapsToSuperleagueOrFeeder1()
    {
        LeagueHierarchy.FromLegacySuperleagueFlag(true).ShouldBe(LeagueLevel.Superleague);
        LeagueHierarchy.FromLegacySuperleagueFlag(false).ShouldBe(LeagueLevel.Feeder1);
    }
}
