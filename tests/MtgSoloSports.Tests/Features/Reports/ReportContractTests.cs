using MtgSoloSports.Features.Reports;
using MtgSoloSports.Features.Reports.StandingsReport;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Leagues;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

public sealed class ReportContractTests
{
    [Fact]
    public void NormalizeFormat_AcceptsJsonAndMd()
    {
        ReportContract.NormalizeFormat(null).ShouldBe("json");
        ReportContract.NormalizeFormat("json").ShouldBe("json");
        ReportContract.NormalizeFormat("JSON").ShouldBe("json");
        ReportContract.NormalizeFormat("md").ShouldBe("md");
        ReportContract.NormalizeFormat("markdown").ShouldBe("md");
        ReportContract.NormalizeFormat("MD").ShouldBe("md");
    }

    [Fact]
    public void NormalizeFormat_RejectsUnknown()
    {
        Should.Throw<ArgumentException>(() => ReportContract.NormalizeFormat("pdf"));
        Should.Throw<ArgumentException>(() => ReportContract.NormalizeFormat("html"));
    }

    [Fact]
    public void Filenames_AreSafeAndScoped()
    {
        string athlete = ReportFilenames.AthleteFileName(42, "Urza, Lord High Artificer!", "md");
        athlete.ShouldBe("athlete-42-urza-lord-high-artificer.md");
        string standings = ReportFilenames.StandingsFileName(3, 7, "Red League F2", "json");
        standings.ShouldBe("standings-season-3-league-7-red-league-f2.json");
    }

    [Fact]
    public void Filenames_HandleLongAndSpecialNames()
    {
        string longName = new string('A', 200) + " ___ !!!";
        string file = ReportFilenames.AthleteFileName(1, longName, "json");
        file.Length.ShouldBeLessThan(80);
        file.ShouldStartWith("athlete-1-");
        file.ShouldEndWith(".json");
        file.ShouldNotContain(" ");
        file.ShouldNotContain("/");
        ReportFilenames.Slug(null).ShouldBe("report");
        ReportFilenames.Slug("   ").ShouldBe("report");
        ReportFilenames.Slug("Äöü — — test").ShouldBe("test");
    }

    [Fact]
    public void Markdown_PreservesThousandthsExactly()
    {
        ReportMarkdown.FormatPoints(77000).ShouldBe("77.000");
        ReportMarkdown.FormatPoints(1).ShouldBe("0.001");
        ReportMarkdown.FormatPoints(0).ShouldBe("0.000");
        ReportMarkdown.FormatBonus(7552).ShouldBe("+7.552%");
        ReportMarkdown.FormatBonus(-100).ShouldBe("-0.100%");
        ReportMarkdown.FormatBonus(0).ShouldBe("+0.000%");
        ReportMarkdown.RankOrDash(null).ShouldContain("not ranked");
        ReportMarkdown.RankOrDash(1).ShouldBe("P1");
        ReportMarkdown.Cell("a|b\nc").ShouldBe("a\\|b c");
    }

    [Theory]
    [InlineData(1, "safe")]
    [InlineData(16, "safe")]
    [InlineData(17, "qualifier")]
    [InlineData(24, "qualifier")]
    [InlineData(25, "relegation")]
    [InlineData(32, "relegation")]
    public void Zones_SuperleagueBands(int rank, string expected)
    {
        ReportZones.ZoneForRank(LeagueLevel.Superleague, rank, true).ShouldBe(expected);
    }

    [Fact]
    public void Zones_ProvisionalCarriesNoZone()
    {
        ReportZones.ZoneForRank(LeagueLevel.Superleague, 1, false).ShouldBeNull();
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 1, false).ShouldBeNull();
    }

    [Fact]
    public void Zones_FeederBands()
    {
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 1, true).ShouldBe("champion");
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 3, true).ShouldBe("qualifier-up");
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 10, true).ShouldBe("safe");
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 20, true).ShouldBe("qualifier-hold");
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 30, true).ShouldBe("relegated");
        ReportZones.ZoneForRank(LeagueLevel.Feeder2, 5, true).ShouldBe("promoted");
        ReportZones.ZoneForRank(LeagueLevel.Feeder3, 30, true).ShouldBe("safe");
    }

    [Fact]
    public void Zones_LegacySingleFeederKeepsOriginalBands()
    {
        ReportZones.IsLegacySingleFeeder(LeagueLevel.Feeder1, FeederDivision.None).ShouldBeTrue();
        ReportZones.IsLegacySingleFeeder(LeagueLevel.Feeder1, FeederDivision.First).ShouldBeFalse();
        ReportZones.IsLegacySingleFeeder(LeagueLevel.Superleague, FeederDivision.None).ShouldBeFalse();

        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 1, true, FeederDivision.None).ShouldBe("champion");
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 3, true, FeederDivision.None).ShouldBe("qualifier");
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 10, true, FeederDivision.None).ShouldBe("safe");
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 20, true, FeederDivision.None).ShouldBe("safe");
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 30, true, FeederDivision.None).ShouldBe("safe");
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 20, false, FeederDivision.None).ShouldBeNull();

        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 20, true, FeederDivision.First).ShouldBe("qualifier-hold");
        ReportZones.ZoneForRank(LeagueLevel.Feeder1, 30, true, FeederDivision.First).ShouldBe("relegated");

        ReportZones.ZoneLabel(LeagueLevel.Feeder1, "qualifier").ShouldBe("Qualifier (2–4)");
        ReportZones.ZoneSummary(LeagueLevel.Feeder1, FeederDivision.None)
            .ShouldBe("Feeder: 1 champion promoted, 2–4 qualifier challengers, 5–32 safe");
        ReportZones.ZoneSummary(LeagueLevel.Feeder1, FeederDivision.First).ShouldContain("17–24 qualifier incumbents");
    }

    [Fact]
    public void MapFinalRows_LegacyFeederMarksLateRanksSafe()
    {
        List<SeasonStandingEntity> rows = Enumerable.Range(1, 32).Select(rank => new SeasonStandingEntity
        {
            SeasonId = 1,
            LeagueId = 7,
            SaveAthleteId = 1000 + rank,
            SeasonRank = rank,
            TotalChampionshipPointsThousandths = 1000 * (33 - rank),
            IsChampion = rank == 1,
        }).ToList();
        Dictionary<int, string> names = rows.ToDictionary(r => r.SaveAthleteId, r => $"Athlete {r.SaveAthleteId}");

        var legacy = GetStandingsReportHandler.MapFinalRows(rows, names, LeagueLevel.Feeder1, FeederDivision.None);
        legacy.Single(r => r.Rank == 1).Zone.ShouldBe("champion");
        legacy.Single(r => r.Rank == 3).Zone.ShouldBe("qualifier");
        legacy.Single(r => r.Rank == 20).Zone.ShouldBe("safe");
        legacy.Single(r => r.Rank == 30).Zone.ShouldBe("safe");
        legacy.Single(r => r.Rank == 20).ZoneLabel.ShouldBe("Safe");
        legacy.Single(r => r.Rank == 3).ZoneLabel.ShouldBe("Qualifier (2–4)");

        var tiered = GetStandingsReportHandler.MapFinalRows(rows, names, LeagueLevel.Feeder1, FeederDivision.First);
        tiered.Single(r => r.Rank == 20).Zone.ShouldBe("qualifier-hold");
        tiered.Single(r => r.Rank == 30).Zone.ShouldBe("relegated");
    }
}
