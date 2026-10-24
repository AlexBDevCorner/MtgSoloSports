using MtgSoloSports.Features.Leagues.Profiles;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Leagues;

public sealed class LeagueProfilesTests
{
    [Fact]
    public void LeagueKey_Superleague_IgnoresDivisionAndColor()
    {
        LeagueKey.Build((int)LeagueKind.Superleague, 0, 0).ShouldBe("superleague");
        LeagueKey.Build((int)LeagueKind.Superleague, 0, 5).ShouldBe("superleague");
    }

    [Fact]
    public void LeagueKey_Feeder_IncludesDivisionAndColor()
    {
        LeagueKey.Build((int)LeagueKind.Feeder, 1, 0).ShouldBe("feeder-1-white");
        LeagueKey.Build((int)LeagueKind.Feeder, 2, 1).ShouldBe("feeder-2-blue");
        LeagueKey.Build((int)LeagueKind.Feeder, 3, 7).ShouldBe("feeder-3-colorless");
    }

    [Fact]
    public void LeagueKey_LegacyFeeder_StaysDistinctFromTiered()
    {
        string legacy = LeagueKey.Build((int)LeagueKind.Feeder, 0, 0);
        string tiered = LeagueKey.Build((int)LeagueKind.Feeder, 1, 0);
        legacy.ShouldBe("feeder-white");
        tiered.ShouldBe("feeder-1-white");
        string.Equals(legacy, tiered, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Theory]
    [InlineData("superleague", 1, 0, null)]
    [InlineData("feeder-1-white", 0, 1, 0)]
    [InlineData("feeder-2-blue", 0, 2, 1)]
    [InlineData("feeder-3-colorless", 0, 3, 7)]
    [InlineData("feeder-white", 0, 0, 0)]
    [InlineData("Feeder-1-White", 0, 1, 0)]
    public void LeagueKey_RoundTrips(string key, int kind, int division, int? color)
    {
        LeagueKey.TryParse(key, out int parsedKind, out int? parsedDivision, out int? parsedColor).ShouldBeTrue();
        parsedKind.ShouldBe(kind);
        parsedDivision.ShouldBe(division);
        parsedColor.ShouldBe(color);
        LeagueKey.Build(parsedKind, parsedDivision!.Value, parsedColor ?? 0).ShouldBe(key.ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pool")]
    [InlineData("feeder")]
    [InlineData("feeder-4-white")]
    [InlineData("superleague-extra")]
    public void LeagueKey_RejectsUnknownKeys(string? key)
    {
        LeagueKey.TryParse(key, out int _, out int? _, out int? _).ShouldBeFalse();
    }

    [Fact]
    public void Aggregator_DynastyStreak_CountsSuccessiveCompletedSeasons()
    {
        var seasons = new List<LeagueProfileAggregator.LeagueSeasonInput>
        {
            Season(1, ("Ava", 1, 2000000), ("Ben", 2, 1900000), ("Cy", 3, 1800000)),
            Season(2, ("Ava", 1, 2100000), ("Cy", 2, 1900000), ("Ben", 3, 1800000)),
            Season(3, ("Ava", 1, 2200000), ("Ben", 2, 2000000), ("Cy", 3, 1900000)),
        };
        var names = Names("Ava", "Ben", "Cy");
        IReadOnlyList<GetLeagueProfileResponse.LeagueRecordEntry> records =
            LeagueProfileAggregator.BuildRecords(seasons, names);

        GetLeagueProfileResponse.LeagueRecordEntry streak = Single(records, "league_longest_title_streak");
        streak.Value.ShouldBe(3);
        streak.Holders.Count.ShouldBe(1);
        streak.Holders[0].AthleteName.ShouldBe("Ava");

        GetLeagueProfileResponse.LeagueRecordEntry titles = Single(records, "league_most_titles");
        titles.Value.ShouldBe(3);
        titles.Holders.Select(h => h.AthleteName).ShouldBe(["Ava"]);

        IReadOnlyList<GetLeagueProfileResponse.AllTimeRow> board =
            LeagueProfileAggregator.BuildLeaderboard(seasons, names);
        board[0].AthleteName.ShouldBe("Ava");
        board[0].TotalLeaguePointsThousandths.ShouldBe(2000000L + 2100000L + 2200000L);
        board[0].CompletedSeasons.ShouldBe(3);

        IReadOnlyList<GetLeagueProfileResponse.PodiumSeason> history =
            LeagueProfileAggregator.BuildPodiumHistory(seasons, names);
        history.Count.ShouldBe(3);
        history[0].SeasonNumber.ShouldBe(3);
        history[0].Champion!.AthleteName.ShouldBe("Ava");
        history[0].WinningMarginThousandths.ShouldBe(200000);
    }

    [Fact]
    public void Aggregator_InterruptedReign_ResetsStreakButKeepsTitles()
    {
        var seasons = new List<LeagueProfileAggregator.LeagueSeasonInput>
        {
            Season(1, ("Ava", 1, 2000000), ("Ben", 2, 1900000)),
            Season(2, ("Ben", 1, 2000000), ("Ava", 2, 1900000)),
            Season(3, ("Ava", 1, 2000000), ("Ben", 2, 1900000)),
        };
        var names = Names("Ava", "Ben");
        IReadOnlyList<GetLeagueProfileResponse.LeagueRecordEntry> records =
            LeagueProfileAggregator.BuildRecords(seasons, names);

        GetLeagueProfileResponse.LeagueRecordEntry streak = Single(records, "league_longest_title_streak");
        streak.Value.ShouldBe(1);
        streak.Holders.Count.ShouldBe(2);

        GetLeagueProfileResponse.LeagueRecordEntry titles = Single(records, "league_most_titles");
        titles.Value.ShouldBe(2);
        titles.Holders.Select(h => h.AthleteName).ShouldBe(["Ava"]);
    }

    [Fact]
    public void Aggregator_TiedPeak_ListsEveryHolderWithSeason()
    {
        var seasons = new List<LeagueProfileAggregator.LeagueSeasonInput>
        {
            Season(1, ("Ava", 1, 2500000), ("Ben", 2, 2000000)),
            Season(2, ("Ben", 1, 2500000), ("Ava", 2, 2400000)),
        };
        var names = Names("Ava", "Ben");
        IReadOnlyList<GetLeagueProfileResponse.LeagueRecordEntry> records =
            LeagueProfileAggregator.BuildRecords(seasons, names);

        GetLeagueProfileResponse.LeagueRecordEntry peak = Single(records, "league_highest_season_points");
        peak.Value.ShouldBe(2500000);
        peak.Holders.Count.ShouldBe(2);
        peak.Holders.Select(h => h.AthleteName).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(["Ava", "Ben"]);
    }

    [Fact]
    public void Aggregator_FewerThanThree_LeavesMissingMedalistsNull()
    {
        var seasons = new List<LeagueProfileAggregator.LeagueSeasonInput>
        {
            new(
                1, 101, 11, (int)LeagueKind.Feeder, 1, 0, "White League", true,
                [
                    new LeagueProfileAggregator.LeagueStandingInput(1, 1, 1000000, true),
                    new LeagueProfileAggregator.LeagueStandingInput(2, 2, 900000, false),
                ]),
        };
        var names = new Dictionary<int, string> { [1] = "Ava", [2] = "Ben" };
        IReadOnlyList<GetLeagueProfileResponse.PodiumSeason> history =
            LeagueProfileAggregator.BuildPodiumHistory(seasons, names);
        history.Count.ShouldBe(1);
        history[0].Champion!.AthleteName.ShouldBe("Ava");
        history[0].RunnerUp!.AthleteName.ShouldBe("Ben");
        history[0].Third.ShouldBeNull();
        history[0].WinningMarginThousandths.ShouldBe(100000);
    }

    [Fact]
    public void Aggregator_OngoingSeason_ExcludedFromAwardsButSurfacedSeparately()
    {
        var seasons = new List<LeagueProfileAggregator.LeagueSeasonInput>
        {
            Season(1, ("Ava", 1, 2000000), ("Ben", 2, 1900000)),
            new(
                2, 102, 11, (int)LeagueKind.Feeder, 1, 0, "White League", false,
                [
                    new LeagueProfileAggregator.LeagueStandingInput(4, 1, 500000, false),
                ]),
        };
        var names = Names("Ava", "Ben", "Dee");
        IReadOnlyList<GetLeagueProfileResponse.PodiumSeason> history =
            LeagueProfileAggregator.BuildPodiumHistory(seasons, names);
        history.Count.ShouldBe(1);
        history[0].SeasonNumber.ShouldBe(1);

        IReadOnlyList<GetLeagueProfileResponse.AllTimeRow> board =
            LeagueProfileAggregator.BuildLeaderboard(seasons, names);
        board.Any(r => string.Equals(r.AthleteName, "Dee", StringComparison.Ordinal)).ShouldBeFalse();
        board.Count.ShouldBe(2);
    }

    [Fact]
    public void Aggregator_Leaderboard_OrdersByPointsThenTitlesThenName()
    {
        var seasons = new List<LeagueProfileAggregator.LeagueSeasonInput>
        {
            Season(1, ("Ava", 1, 2000000), ("Ben", 2, 2000000)),
            Season(2, ("Ben", 1, 2000000), ("Ava", 2, 2000000)),
        };
        // Ava and Ben both total 4000000; both hold one title; name breaks the tie.
        var names = Names("Ava", "Ben");
        IReadOnlyList<GetLeagueProfileResponse.AllTimeRow> board =
            LeagueProfileAggregator.BuildLeaderboard(seasons, names);
        board.Count.ShouldBe(2);
        board[0].TotalLeaguePointsThousandths.ShouldBe(4000000);
        board[1].TotalLeaguePointsThousandths.ShouldBe(4000000);
        board[0].AthleteName.ShouldBe("Ava");
    }

    [Fact]
    public async Task Overview_Season1Complete_ListsEveryFeederOnceWithChampion()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-league-profiles-");
        try
        {
            var overviewHandler = new ListLeagueProfilesHandler(store);
            ListLeagueProfilesResponse overview = await overviewHandler.HandleAsync(saveId);
            overview.Leagues.Count.ShouldBe(24);
            overview.Leagues.Select(l => l.LeagueKey).Distinct(StringComparer.Ordinal).Count().ShouldBe(24);
            overview.Leagues.All(l => l.CompletedSeasons == 1).ShouldBeTrue();
            overview.Leagues.All(l => l.LatestChampion is not null).ShouldBeTrue();
            overview.Leagues.All(l => l.UniqueAthletes == 32).ShouldBeTrue();
            overview.Leagues.Any(l => string.Equals(l.LeagueKey, "superleague", StringComparison.Ordinal))
                .ShouldBeFalse();

            string key = overview.Leagues
                .OrderBy(l => l.LeagueKey, StringComparer.Ordinal)
                .First()
                .LeagueKey;
            var profileHandler = new GetLeagueProfileHandler(store);
            GetLeagueProfileResponse profile = await profileHandler.HandleAsync(saveId, key);
            profile.LeagueKey.ShouldBe(key);
            profile.CompletedSeasons.ShouldBe(1);
            profile.PodiumHistory.Count.ShouldBe(1);
            profile.AllTimeLeaderboard.Count.ShouldBe(32);
            profile.AllTimeLeaderboard.All(r => r.CompletedSeasons == 1).ShouldBeTrue();
            // All-time totals must equal the single completed final table exactly.
            long podiumTotal = profile.PodiumHistory[0].Champion!.TotalChampionshipPointsThousandths
                + profile.PodiumHistory[0].RunnerUp!.TotalChampionshipPointsThousandths
                + profile.PodiumHistory[0].Third!.TotalChampionshipPointsThousandths;
            long boardTop3 = profile.AllTimeLeaderboard
                .OrderBy(r => r.BestRank)
                .Take(3)
                .Sum(r => r.TotalLeaguePointsThousandths);
            boardTop3.ShouldBe(podiumTotal);
            profile.Records.Any(r => string.Equals(r.RecordKey, "league_most_titles", StringComparison.Ordinal))
                .ShouldBeTrue();
            profile.Records.Any(r => string.Equals(r.RecordKey, "league_longest_title_streak", StringComparison.Ordinal))
                .ShouldBeTrue();
            profile.Records.Any(r => string.Equals(r.RecordKey, "league_largest_winning_margin", StringComparison.Ordinal))
                .ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Profile_UnknownKey_ThrowsNotFound()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-league-unknown-");
        try
        {
            var handler = new GetLeagueProfileHandler(store);
            await Should.ThrowAsync<LeagueProfileNotFoundException>(handler.HandleAsync(saveId, "superleague"));
            await Should.ThrowAsync<LeagueProfileNotFoundException>(handler.HandleAsync(saveId, "nope"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int IdFor(string name)
    {
        return name switch
        {
            "Ava" => 1,
            "Ben" => 2,
            "Cy" => 3,
            "Dee" => 4,
            _ => throw new InvalidOperationException($"Unknown test athlete '{name}'."),
        };
    }

    private static LeagueProfileAggregator.LeagueSeasonInput Season(
        int seasonNumber,
        params (string Name, int Rank, int Points)[] rows)
    {
        List<LeagueProfileAggregator.LeagueStandingInput> inputs = rows
            .Select(row => new LeagueProfileAggregator.LeagueStandingInput(
                IdFor(row.Name), row.Rank, row.Points, row.Rank == 1))
            .ToList();
        return new LeagueProfileAggregator.LeagueSeasonInput(
            seasonNumber, 100 + seasonNumber, 11,
            (int)LeagueKind.Feeder, 1, 0, "White League", true, inputs);
    }

    private static Dictionary<int, string> Names(params string[] names)
    {
        Dictionary<int, string> map = new();
        foreach (string name in names)
        {
            map[IdFor(name)] = name;
        }

        return map;
    }

    private static GetLeagueProfileResponse.LeagueRecordEntry Single(
        IReadOnlyList<GetLeagueProfileResponse.LeagueRecordEntry> records,
        string key)
    {
        return records.Single(r => string.Equals(r.RecordKey, key, StringComparison.Ordinal));
    }
}
