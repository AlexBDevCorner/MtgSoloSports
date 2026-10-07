using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Scoring;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

/// <summary>
/// MSS-061: scalable Type Cup tournament-format mathematics. Covers direct
/// Final for 1-32 teams, qualification-group count ceil(N/32), balanced sizes,
/// exact 32-finalist quotas, deterministic random draw, and the new-format
/// 32-team ceiling that replaces the legacy greater-than-32 minimum-point path.
/// </summary>
public sealed class TypeCupTournamentFormatTests
{
    public static TheoryData<int, int> GroupCounts()
    {
        var data = new TheoryData<int, int>();
        data.Add(1, 0);
        data.Add(2, 0);
        data.Add(31, 0);
        data.Add(32, 0);
        data.Add(33, 2);
        data.Add(35, 2);
        data.Add(64, 2);
        data.Add(65, 3);
        data.Add(96, 3);
        data.Add(97, 4);
        data.Add(150, 5);
        return data;
    }

    [Theory]
    [MemberData(nameof(GroupCounts))]
    public void GroupCount_IsCeilOver32_OrZeroForDirectFinal(int teamCount, int expectedGroups)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        TypeCupTournamentFormat.QualificationGroupCount(teamCount, rules).ShouldBe(expectedGroups);
        TypeCupTournamentFormat.IsDirectFinal(teamCount, rules).ShouldBe(expectedGroups == 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(35)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(96)]
    [InlineData(97)]
    [InlineData(150)]
    public void GroupSizes_AreBalanced_AndWithin32(int teamCount)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(teamCount, rules);
        if (teamCount <= 32)
        {
            sizes.Count.ShouldBe(0);
            return;
        }

        int expectedGroups = (teamCount + 31) / 32;
        sizes.Count.ShouldBe(expectedGroups);
        sizes.Sum().ShouldBe(teamCount);
        sizes.Max().ShouldBeLessThanOrEqualTo(32);
        (sizes.Max() - sizes.Min()).ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void ThirtyThreeToSixtyFour_GivesTwoBalancedGroups()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        TypeCupTournamentFormat.BalancedQualificationGroupSizes(33, rules).ShouldBe([17, 16]);
        TypeCupTournamentFormat.BalancedQualificationGroupSizes(35, rules).ShouldBe([18, 17]);
        TypeCupTournamentFormat.BalancedQualificationGroupSizes(64, rules).ShouldBe([32, 32]);
    }

    [Fact]
    public void SixtyFiveToNinetySix_GivesThreeBalancedGroups()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        TypeCupTournamentFormat.BalancedQualificationGroupSizes(65, rules).ShouldBe([22, 22, 21]);
        TypeCupTournamentFormat.BalancedQualificationGroupSizes(96, rules).ShouldBe([32, 32, 32]);
    }

    [Fact]
    public void NinetySeven_GivesFourGroups()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        TypeCupTournamentFormat.BalancedQualificationGroupSizes(97, rules).ShouldBe([25, 24, 24, 24]);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(35)]
    [InlineData(64)]
    public void TwoGroups_AdvanceSixteenEach(int teamCount)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(teamCount, rules);
        IReadOnlyList<int> quotas = TypeCupTournamentFormat.AllocateFinalPlaces(sizes, rules);
        quotas.ShouldBe([16, 16]);
        quotas.Sum().ShouldBe(32);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(96)]
    public void ThreeGroups_AdvanceElevenElevenTen(int teamCount)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(teamCount, rules);
        IReadOnlyList<int> quotas = TypeCupTournamentFormat.AllocateFinalPlaces(sizes, rules);
        quotas.ShouldBe([11, 11, 10]);
        quotas.Sum().ShouldBe(32);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(35)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(96)]
    [InlineData(97)]
    [InlineData(150)]
    public void Quotas_AlwaysTotal32_AndNeverExceedGroupSize(int teamCount)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        if (teamCount <= 32)
        {
            return;
        }

        IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(teamCount, rules);
        IReadOnlyList<int> quotas = TypeCupTournamentFormat.AllocateFinalPlaces(sizes, rules);
        quotas.Count.ShouldBe(sizes.Count);
        quotas.Sum().ShouldBe(32);
        for (int i = 0; i < sizes.Count; i++)
        {
            quotas[i].ShouldBeLessThanOrEqualTo(sizes[i]);
        }
    }

    [Fact]
    public void LargerGroups_ReceiveExtraPlace_BeforeEqualSized()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        // 70 teams: sizes [24, 23, 23]; quotas base 10 remainder 2 -> larger group first.
        IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(70, rules);
        sizes.ShouldBe([24, 23, 23]);
        IReadOnlyList<int> quotas = TypeCupTournamentFormat.AllocateFinalPlaces(sizes, rules);
        quotas.Sum().ShouldBe(32);
        quotas[0].ShouldBeGreaterThanOrEqualTo(quotas[1]);
        quotas[0].ShouldBeGreaterThanOrEqualTo(quotas[2]);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(35)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(96)]
    [InlineData(97)]
    [InlineData(150)]
    public void Draw_AssignsEveryTeamExactlyOnce(int teamCount)
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<string> types = Enumerable.Range(0, teamCount).Select(i => $"Type-{i:D3}").ToList();
        Pcg32V1 rng = new(42421UL, 7771UL);
        var assignments = TypeCupTournamentFormat.DrawQualificationGroups(types, rng, rules);
        assignments.Count.ShouldBe(teamCount);
        assignments.Select(a => a.CreatureType).OrderBy(t => t, StringComparer.Ordinal)
            .ShouldBe(types.OrderBy(t => t, StringComparer.Ordinal).ToList());
        assignments.Select(a => a.CreatureType).Distinct(StringComparer.Ordinal).Count().ShouldBe(teamCount);

        IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(teamCount, rules);
        IReadOnlyList<int> quotas = TypeCupTournamentFormat.AllocateFinalPlaces(sizes, rules);
        Should.NotThrow(() => TypeCupTournamentFormat.ValidateTournamentDraw(types, assignments, sizes, quotas, rules));
    }

    [Fact]
    public void Draw_IsDeterministic_UnderIdenticalRngState()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<string> types = Enumerable.Range(0, 35).Select(i => $"Type-{i:D3}").ToList();
        var first = TypeCupTournamentFormat.DrawQualificationGroups(types, new Pcg32V1(42421UL, 7771UL), rules);
        var second = TypeCupTournamentFormat.DrawQualificationGroups(types, new Pcg32V1(42421UL, 7771UL), rules);
        first.Select(a => (a.CreatureType, a.QualificationGroup))
            .ShouldBe(second.Select(a => (a.CreatureType, a.QualificationGroup)).ToList());

        IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(35, rules);
        string firstChecksum = TypeCupTournamentFormat.ComputeDrawChecksum(first, 35, sizes);
        string secondChecksum = TypeCupTournamentFormat.ComputeDrawChecksum(second, 35, sizes);
        firstChecksum.ShouldBe(secondChecksum);
    }

    [Fact]
    public void Draw_Differs_UnderDifferentSeed()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<string> types = Enumerable.Range(0, 35).Select(i => $"Type-{i:D3}").ToList();
        var first = TypeCupTournamentFormat.DrawQualificationGroups(types, new Pcg32V1(42421UL, 7771UL), rules);
        var second = TypeCupTournamentFormat.DrawQualificationGroups(types, new Pcg32V1(99991UL, 12341UL), rules);
        IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(35, rules);
        string firstChecksum = TypeCupTournamentFormat.ComputeDrawChecksum(first, 35, sizes);
        string secondChecksum = TypeCupTournamentFormat.ComputeDrawChecksum(second, 35, sizes);
        firstChecksum.Equals(secondChecksum, StringComparison.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void Draw_IsNotSeededByStrength()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        // Reverse input order must still produce the same draw because the kernel
        // canonicalizes by ordinal sort before shuffling.
        List<string> ordered = Enumerable.Range(0, 35).Select(i => $"Type-{i:D3}").ToList();
        List<string> reversed = ordered.AsEnumerable().Reverse().ToList();
        var fromOrdered = TypeCupTournamentFormat.DrawQualificationGroups(ordered, new Pcg32V1(42421UL, 7771UL), rules);
        var fromReversed = TypeCupTournamentFormat.DrawQualificationGroups(reversed, new Pcg32V1(42421UL, 7771UL), rules);
        Dictionary<string, int> orderedMap = fromOrdered.ToDictionary(a => a.CreatureType, a => a.QualificationGroup, StringComparer.Ordinal);
        Dictionary<string, int> reversedMap = fromReversed.ToDictionary(a => a.CreatureType, a => a.QualificationGroup, StringComparer.Ordinal);
        orderedMap.Count.ShouldBe(reversedMap.Count);
        foreach (string type in ordered)
        {
            orderedMap[type].ShouldBe(reversedMap[type]);
        }
    }

    [Fact]
    public void NewFormat_NeverUsesBeyond32Scoring()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        // New-format strict path rejects positions beyond the table.
        Should.Throw<ArgumentOutOfRangeException>(() => ScoringCalculator.TypeCupBasePointsForNewFormatPosition(33, rules));
        Should.Throw<ArgumentOutOfRangeException>(() => ScoringCalculator.TypeCupBasePointsForNewFormatPosition(64, rules));
        Should.Throw<ArgumentOutOfRangeException>(() => TypeCupTournamentFormat.ValidateCompetitionFieldSize(33, rules));

        // Every qualification group and the Final fit the strict path.
        foreach (int teamCount in new[] { 33, 35, 64, 65, 96, 97, 150 })
        {
            IReadOnlyList<int> sizes = TypeCupTournamentFormat.BalancedQualificationGroupSizes(teamCount, rules);
            foreach (int size in sizes)
            {
                Should.NotThrow(() => TypeCupTournamentFormat.ValidateCompetitionFieldSize(size, rules));
                for (int position = 1; position <= size; position++)
                {
                    Should.NotThrow(() => ScoringCalculator.TypeCupBasePointsForNewFormatPosition(position, rules));
                }
            }

            Should.NotThrow(() => TypeCupTournamentFormat.ValidateCompetitionFieldSize(rules.TypeCupFinalTeamCount, rules));
        }
    }

    [Fact]
    public void LegacyBeyond32Scoring_RemainsForHistoricalReads()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        // Historical single-field Cups (pre-MSS-061) still decode positions beyond 32.
        ScoringCalculator.TypeCupBasePointsForPosition(33, rules).Thousandths.ShouldBe(1000);
        ScoringCalculator.TypeCupBasePointsForPosition(64, rules).Thousandths.ShouldBe(1000);
    }

    [Fact]
    public void Rules_DefaultToScalableFormat_With32TeamCeilings()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        rules.TypeCupTournamentFormatVersion.ShouldBe(1);
        rules.TypeCupMaxDirectFinalTeams.ShouldBe(32);
        rules.TypeCupFinalTeamCount.ShouldBe(32);
        Should.NotThrow(() => rules.Validate());
    }

    [Fact]
    public void Rules_LegacyFormatVersion_DecodesForOldSnapshots()
    {
        RulesV1 legacy = RulesV1.Create(new RulesV1Overrides { TypeCupTournamentFormatVersion = 0 });
        legacy.TypeCupTournamentFormatVersion.ShouldBe(0);
        Should.NotThrow(() => legacy.Validate());
    }
}
