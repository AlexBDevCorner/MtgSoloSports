using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

/// <summary>
/// MSS-071: equal guaranteed quotas plus performance wildcards.
/// </summary>
public sealed class TypeCupWildcardTests
{
    public static TheoryData<int, int, int> GuaranteedAndWildcards()
    {
        var data = new TheoryData<int, int, int>();
        data.Add(33, 16, 0);
        data.Add(35, 16, 0);
        data.Add(64, 16, 0);
        data.Add(65, 10, 2);
        data.Add(83, 10, 2);
        data.Add(84, 10, 2);
        data.Add(85, 10, 2);
        data.Add(96, 10, 2);
        data.Add(97, 8, 0);
        data.Add(150, 6, 2);
        return data;
    }

    [Theory]
    [MemberData(nameof(GuaranteedAndWildcards))]
    public void GuaranteedPlusWildcards_Total32(int teamCount, int expectedBase, int expectedWildcards)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        int groups = TypeCupTournamentFormat.QualificationGroupCount(teamCount, rules);
        TypeCupTournamentFormat.GuaranteedPlacesPerGroup(groups, rules).ShouldBe(expectedBase);
        TypeCupTournamentFormat.WildcardCount(groups, rules).ShouldBe(expectedWildcards);
        IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(teamCount, rules);
        IReadOnlyList<int> guaranteed = TypeCupTournamentFormat.AllocateGuaranteedFinalPlaces(sizes, rules);
        Assert.All(guaranteed, g => g.ShouldBe(expectedBase));
        (guaranteed.Sum() + expectedWildcards).ShouldBe(32);
    }

    [Fact]
    public void EightyFour_GivesThreeGroupsOf28_WithTenGuaranteedPlusTwoWildcards()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        TypeCupTournamentFormat.BalancedQualificationGroupSizes(84, rules).ShouldBe([28, 28, 28]);
        TypeCupTournamentFormat.AllocateGuaranteedFinalPlaces([28, 28, 28], rules).ShouldBe([10, 10, 10]);
        TypeCupTournamentFormat.WildcardCount(3, rules).ShouldBe(2);
    }

    [Fact]
    public void EqualSizes_ReduceToRawTotals()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        TypeCupTournamentFormat.WildcardCandidate low = new(1, "A-Team", 11, 1000000, 900000, 28);
        TypeCupTournamentFormat.WildcardCandidate high = new(2, "B-Team", 11, 1000001, 900000, 28);
        TypeCupTournamentFormat.CompareWildcardCandidates(low, high, rules).ShouldBeGreaterThan(0);
        TypeCupTournamentFormat.CompareWildcardCandidates(high, low, rules).ShouldBeLessThan(0);
    }

    [Fact]
    public void ResolveWildcards_AdvancesBest_ByAdjustedScore()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<TypeCupTournamentFormat.WildcardCandidate> candidates =
        [
            new(1, "A-Team", 11, 3000000, 2900000, 28),
            new(2, "B-Team", 11, 2000000, 1900000, 28),
            new(3, "C-Team", 11, 1000000, 900000, 28),
        ];
        Pcg32V1 rng = new(1234UL, 5678UL);
        Pcg32State before = rng.Snapshot();
        TypeCupTournamentFormat.WildcardResolution resolution =
            TypeCupTournamentFormat.ResolveWildcards(candidates, 2, rng, rules);
        resolution.Winners.Select(w => w.Team).ShouldBe(["A-Team", "B-Team"]);
        resolution.Eliminated.Select(w => w.Team).ShouldBe(["C-Team"]);
        resolution.TieDrawConsumed.ShouldBeFalse();
        rng.Snapshot().ShouldBe(before);
    }

    [Fact]
    public void ResolveWildcards_GroupNumbers_DoNotChangeWinners()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<TypeCupTournamentFormat.WildcardCandidate> first =
        [
            new(1, "A-Team", 11, 3000000, 2900000, 28),
            new(2, "B-Team", 11, 2000000, 1900000, 28),
            new(3, "C-Team", 11, 1000000, 900000, 28),
        ];
        List<TypeCupTournamentFormat.WildcardCandidate> swapped =
        [
            new(3, "A-Team", 11, 3000000, 2900000, 28),
            new(1, "B-Team", 11, 2000000, 1900000, 28),
            new(2, "C-Team", 11, 1000000, 900000, 28),
        ];
        TypeCupTournamentFormat.WildcardResolution firstWinners =
            TypeCupTournamentFormat.ResolveWildcards(first, 2, new Pcg32V1(42UL, 99UL), rules);
        TypeCupTournamentFormat.WildcardResolution swappedWinners =
            TypeCupTournamentFormat.ResolveWildcards(swapped, 2, new Pcg32V1(42UL, 99UL), rules);
        firstWinners.Winners.Select(w => w.Team).OrderBy(t => t, StringComparer.Ordinal)
            .ShouldBe(swappedWinners.Winners.Select(w => w.Team).OrderBy(t => t, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void GroupThreeRankEleven_CanTakeWildcard_WhenOutperforming()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<TypeCupTournamentFormat.WildcardCandidate> candidates =
        [
            new(1, "Group1-Eleventh", 11, 1500000, 1400000, 28),
            new(2, "Group2-Eleventh", 11, 1600000, 1500000, 28),
            new(3, "Group3-Eleventh", 11, 2500000, 2400000, 28),
        ];
        TypeCupTournamentFormat.WildcardResolution resolution =
            TypeCupTournamentFormat.ResolveWildcards(candidates, 2, new Pcg32V1(11UL, 22UL), rules);
        resolution.Winners.Select(w => w.Team).ToList().Contains("Group3-Eleventh", StringComparer.Ordinal).ShouldBeTrue();
        resolution.Eliminated.Single().Team.ShouldBe("Group1-Eleventh");
    }

    [Fact]
    public void ResolveWildcards_TieAtCutoff_ConsumesRng_Deterministically()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<TypeCupTournamentFormat.WildcardCandidate> candidates =
        [
            new(1, "A-Team", 11, 2000000, 1900000, 28),
            new(2, "B-Team", 11, 2000000, 1900000, 28),
            new(3, "C-Team", 11, 2000000, 1900000, 28),
        ];
        TypeCupTournamentFormat.WildcardResolution first =
            TypeCupTournamentFormat.ResolveWildcards(candidates, 2, new Pcg32V1(777UL, 888UL), rules);
        TypeCupTournamentFormat.WildcardResolution second =
            TypeCupTournamentFormat.ResolveWildcards(candidates, 2, new Pcg32V1(777UL, 888UL), rules);
        first.TieDrawConsumed.ShouldBeTrue();
        first.Winners.Count.ShouldBe(2);
        first.Winners.Select(w => w.Team).OrderBy(t => t, StringComparer.Ordinal)
            .ShouldBe(second.Winners.Select(w => w.Team).OrderBy(t => t, StringComparer.Ordinal).ToList());
    }
}
