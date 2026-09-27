using MtgSoloSports.Features.Records;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Records;

public sealed class RecordCalculatorTests
{
    [Fact]
    public void BuildRecord_VacantWhenMaxZero()
    {
        Dictionary<int, string> names = new() { [1] = "Alpha", [2] = "Beta" };
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            names,
            [],
            [],
            [],
            [
                new RecordCalculator.CareerInput(1, 0, 0, 0),
                new RecordCalculator.CareerInput(2, 0, 0, 0),
            ],
            [
                new RecordCalculator.PromotionInput(1, 0, 0),
                new RecordCalculator.PromotionInput(2, 0, 0),
            ]);
        foreach (RecordCalculator.RecordHolders record in computed)
        {
            record.Value.ShouldBe(0);
            record.HolderAthleteIds.ShouldBeEmpty();
        }
    }

    [Fact]
    public void Tie_ReturnsAllHoldersOrderedByNameThenId()
    {
        Dictionary<int, string> names = new() { [1] = "Beta", [2] = "Alpha", [3] = "Gamma" };
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            names,
            [],
            [],
            [
                new RecordCalculator.MembershipInput(1, 1, 1, (int)LeagueKind.Feeder, true),
                new RecordCalculator.MembershipInput(1, 1, 2, (int)LeagueKind.Feeder, true),
                new RecordCalculator.MembershipInput(1, 1, 3, (int)LeagueKind.Feeder, true),
            ],
            [
                new RecordCalculator.CareerInput(1, 0, 5, 0),
                new RecordCalculator.CareerInput(2, 0, 5, 0),
                new RecordCalculator.CareerInput(3, 0, 3, 0),
            ],
            [
                new RecordCalculator.PromotionInput(1, 0, 0),
                new RecordCalculator.PromotionInput(2, 0, 0),
                new RecordCalculator.PromotionInput(3, 0, 0),
            ]);
        RecordCalculator.RecordHolders stageWins = computed.Single(r => string.Equals(r.RecordKey, RecordKey.StageWins, StringComparison.Ordinal));
        stageWins.Value.ShouldBe(5);
        stageWins.HolderAthleteIds.ShouldBe([2, 1]);
    }

    [Fact]
    public void Replacement_OnlyHigherValueReplacesHolders()
    {
        Dictionary<int, string> names = new() { [1] = "Alpha", [2] = "Beta" };
        IReadOnlyList<RecordCalculator.RecordHolders> tied = RecordCalculator.ComputeAll(
            names,
            [],
            [],
            [],
            [
                new RecordCalculator.CareerInput(1, 7, 0, 0),
                new RecordCalculator.CareerInput(2, 7, 0, 0),
            ],
            [
                new RecordCalculator.PromotionInput(1, 0, 0),
                new RecordCalculator.PromotionInput(2, 0, 0),
            ]);
        tied.Single(r => string.Equals(r.RecordKey, RecordKey.RoundWins, StringComparison.Ordinal)).HolderAthleteIds.ShouldBe([1, 2]);

        IReadOnlyList<RecordCalculator.RecordHolders> replaced = RecordCalculator.ComputeAll(
            names,
            [],
            [],
            [],
            [
                new RecordCalculator.CareerInput(1, 7, 0, 0),
                new RecordCalculator.CareerInput(2, 9, 0, 0),
            ],
            [
                new RecordCalculator.PromotionInput(1, 0, 0),
                new RecordCalculator.PromotionInput(2, 0, 0),
            ]);
        RecordCalculator.RecordHolders roundWins = replaced.Single(r => string.Equals(r.RecordKey, RecordKey.RoundWins, StringComparison.Ordinal));
        roundWins.Value.ShouldBe(9);
        roundWins.HolderAthleteIds.ShouldBe([2]);
    }

    [Fact]
    public void Titles_CountFeederAndSuperleagueSeparately()
    {
        Dictionary<int, string> names = new() { [1] = "Alpha", [2] = "Beta" };
        List<RecordCalculator.SeasonStandingInput> standings =
        [
            new(10, 1, (int)LeagueKind.Feeder, 1, true),
            new(11, 1, (int)LeagueKind.Feeder, 2, true),
            new(20, 2, (int)LeagueKind.Superleague, 1, true),
        ];
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            names,
            standings,
            [],
            [],
            [
                new RecordCalculator.CareerInput(1, 0, 0, 0),
                new RecordCalculator.CareerInput(2, 0, 0, 0),
            ],
            [
                new RecordCalculator.PromotionInput(1, 0, 0),
                new RecordCalculator.PromotionInput(2, 0, 0),
            ]);
        computed.Single(r => string.Equals(r.RecordKey, RecordKey.FeederTitles, StringComparison.Ordinal)).Value.ShouldBe(1);
        computed.Single(r => string.Equals(r.RecordKey, RecordKey.FeederTitles, StringComparison.Ordinal)).HolderAthleteIds.ShouldBe([1, 2]);
        RecordCalculator.RecordHolders super = computed.Single(r => string.Equals(r.RecordKey, RecordKey.SuperleagueTitles, StringComparison.Ordinal));
        super.Value.ShouldBe(1);
        super.HolderAthleteIds.ShouldBe([1]);
        RecordCalculator.RecordHolders total = computed.Single(r => string.Equals(r.RecordKey, RecordKey.TotalTitles, StringComparison.Ordinal));
        total.Value.ShouldBe(2);
        total.HolderAthleteIds.ShouldBe([1]);
    }

    [Fact]
    public void LongestTenure_GapBreaksRun()
    {
        Dictionary<int, string> names = new() { [1] = "Alpha", [2] = "Beta" };
        List<RecordCalculator.MembershipInput> memberships =
        [
            new(1, 2, 1, (int)LeagueKind.Superleague, true),
            new(2, 3, 1, (int)LeagueKind.Superleague, true),
            new(3, 4, 1, (int)LeagueKind.Superleague, true),
            new(1, 2, 2, (int)LeagueKind.Superleague, true),
            new(3, 4, 2, (int)LeagueKind.Superleague, true),
        ];
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            names,
            [],
            [],
            memberships,
            [
                new RecordCalculator.CareerInput(1, 0, 0, 0),
                new RecordCalculator.CareerInput(2, 0, 0, 0),
            ],
            [
                new RecordCalculator.PromotionInput(1, 0, 0),
                new RecordCalculator.PromotionInput(2, 0, 0),
            ]);
        RecordCalculator.RecordHolders tenure = computed.Single(r => string.Equals(r.RecordKey, RecordKey.LongestSuperleagueTenure, StringComparison.Ordinal));
        tenure.Value.ShouldBe(3);
        tenure.HolderAthleteIds.ShouldBe([1]);
    }

    [Fact]
    public void TitleStreak_ConsecutiveSeasons()
    {
        Dictionary<int, string> names = new() { [1] = "Alpha", [2] = "Beta" };
        List<RecordCalculator.SeasonStandingInput> standings =
        [
            new(1, 1, (int)LeagueKind.Feeder, 1, true),
            new(2, 2, (int)LeagueKind.Feeder, 1, true),
            new(3, 3, (int)LeagueKind.Feeder, 1, true),
            new(1, 1, (int)LeagueKind.Feeder, 2, true),
            new(3, 3, (int)LeagueKind.Feeder, 2, true),
        ];
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            names,
            standings,
            [],
            [],
            [
                new RecordCalculator.CareerInput(1, 0, 0, 0),
                new RecordCalculator.CareerInput(2, 0, 0, 0),
            ],
            [
                new RecordCalculator.PromotionInput(1, 0, 0),
                new RecordCalculator.PromotionInput(2, 0, 0),
            ]);
        RecordCalculator.RecordHolders streak = computed.Single(r => string.Equals(r.RecordKey, RecordKey.LongestTitleStreak, StringComparison.Ordinal));
        streak.Value.ShouldBe(3);
        streak.HolderAthleteIds.ShouldBe([1]);
    }

    [Fact]
    public void StageWinStreak_SpansConsecutiveSeasonBoundary()
    {
        Dictionary<int, string> names = new() { [1] = "Alpha" };
        List<RecordCalculator.StageStandingInput> stages =
        [
            new(1, 1, 31, 1, 2),
            new(1, 1, 32, 1, 1),
            new(2, 2, 1, 1, 1),
            new(2, 2, 2, 1, 1),
            new(2, 2, 3, 1, 4),
        ];
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            names,
            [],
            stages,
            [],
            [new RecordCalculator.CareerInput(1, 0, 0, 0)],
            [new RecordCalculator.PromotionInput(1, 0, 0)]);
        computed.Single(r => string.Equals(r.RecordKey, RecordKey.LongestStageWinStreak, StringComparison.Ordinal)).Value.ShouldBe(3);
    }

    [Fact]
    public void HighestBonus_UsesThousandthsInteger()
    {
        Dictionary<int, string> names = new() { [1] = "Alpha", [2] = "Beta" };
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            names,
            [],
            [],
            [],
            [
                new RecordCalculator.CareerInput(1, 0, 0, 1250),
                new RecordCalculator.CareerInput(2, 0, 0, 900),
            ],
            [
                new RecordCalculator.PromotionInput(1, 0, 0),
                new RecordCalculator.PromotionInput(2, 0, 0),
            ]);
        RecordCalculator.RecordHolders bonus = computed.Single(r => string.Equals(r.RecordKey, RecordKey.HighestEffectiveBonus, StringComparison.Ordinal));
        bonus.Value.ShouldBe(1250);
        bonus.HolderAthleteIds.ShouldBe([1]);
    }

    [Fact]
    public void ComputeAll_CoversEveryKnownKey()
    {
        Dictionary<int, string> names = new() { [1] = "Alpha" };
        IReadOnlyList<RecordCalculator.RecordHolders> computed = RecordCalculator.ComputeAll(
            names, [], [], [],
            [new RecordCalculator.CareerInput(1, 0, 0, 0)],
            [new RecordCalculator.PromotionInput(1, 0, 0)]);
        computed.Select(r => r.RecordKey).OrderBy(k => k, StringComparer.Ordinal)
            .ShouldBe(RecordKey.All.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }
}
