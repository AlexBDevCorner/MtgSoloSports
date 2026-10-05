using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

public sealed class TypeCupAllocationTests
{
    private static RulesV1 Rules() => RulesV1.CreateDefault();

    private static TypeCupAllocation.CandidateRaw Candidate(
        int id,
        string name,
        string[] types,
        string? capped = null,
        int bonus = 0,
        int performance = 0,
        int form = 0,
        int prestige = 0)
    {
        return new TypeCupAllocation.CandidateRaw(
            id, name, types, capped, bonus, performance, form, prestige);
    }

    [Fact]
    public void CappedAthlete_MayRepresentOnlyNationality()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "Alpha", ["Human", "Wizard"], capped: "Human"),
            Candidate(2, "Beta", ["Human"], bonus: 10),
            Candidate(3, "Gamma", ["Human"], bonus: 10),
            Candidate(4, "Delta", ["Human"], bonus: 10),
            Candidate(5, "Epsilon", ["Wizard"], bonus: 1000),
            Candidate(6, "Zeta", ["Wizard"], bonus: 1000),
            Candidate(7, "Eta", ["Wizard"], bonus: 1000),
            Candidate(8, "Theta", ["Wizard"], bonus: 1000),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        // Alpha is capped for Human and must not appear on the Wizard team.
        TypeCupAllocation.AllocatedTeam human = result.Teams.Single(t => string.Equals(t.CreatureType, "Human", StringComparison.Ordinal));
        TypeCupAllocation.AllocatedTeam wizard = result.Teams.Single(t => string.Equals(t.CreatureType, "Wizard", StringComparison.Ordinal));
        human.Members.Select(m => m.Name).Contains("Alpha", StringComparer.Ordinal).ShouldBeTrue();
        wizard.Members.Select(m => m.Name).Contains("Alpha", StringComparer.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void UncappedAthlete_PrefersStrongerRelativeRank()
    {
        RulesV1 rules = Rules();

        // Star is #4 Human (three stronger Humans) but #1 Wizard (all other
        // Wizards are weaker), so Star prefers Wizard. Human already has four
        // exclusive members and does not need Star; Wizard needs Star to reach
        // four. Preference places Star on the Wizard team while both teams form.
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "Star", ["Human", "Wizard"], bonus: 500),
            Candidate(2, "HumanA", ["Human"], bonus: 900),
            Candidate(3, "HumanB", ["Human"], bonus: 800),
            Candidate(4, "HumanC", ["Human"], bonus: 700),
            Candidate(5, "HumanD", ["Human"], bonus: 600),
            Candidate(6, "WizardB", ["Wizard"], bonus: 100),
            Candidate(7, "WizardC", ["Wizard"], bonus: 100),
            Candidate(8, "WizardD", ["Wizard"], bonus: 100),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        result.Teams.Count.ShouldBe(2);
        TypeCupAllocation.AllocatedTeam wizard = result.Teams.Single(t => string.Equals(t.CreatureType, "Wizard", StringComparison.Ordinal));
        wizard.Members.Select(m => m.Name).Contains("Star", StringComparer.Ordinal).ShouldBeTrue();
        TypeCupAllocation.AllocatedTeam human = result.Teams.Single(t => string.Equals(t.CreatureType, "Human", StringComparison.Ordinal));
        human.Members.Select(m => m.Name).Contains("Star", StringComparer.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void SameFourHumanWizards_FormsExactlyOneTeam()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "Athlete A", ["Human", "Wizard"]),
            Candidate(2, "Athlete B", ["Human", "Wizard"]),
            Candidate(3, "Athlete C", ["Human", "Wizard"]),
            Candidate(4, "Athlete D", ["Human", "Wizard"]),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        // Four distinct athletes can field only one of the two contested teams.
        result.Teams.Count.ShouldBe(1);
        result.Teams[0].Members.Count.ShouldBe(4);
        result.Teams[0].Members.Select(m => m.SelectionRank).OrderBy(r => r).ShouldBe([1, 2, 3, 4]);

        // Deterministic tie-break: Human sorts before Wizard.
        result.Teams[0].CreatureType.ShouldBe("Human");

        HashSet<int> assigned = result.Teams.SelectMany(t => t.Members).Select(m => m.AthleteId).ToHashSet();
        assigned.Count.ShouldBe(4);
    }

    [Fact]
    public void OverlappingTypes_MaximizesTwoTeams_WhenFeasible()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "A1", ["Elves"]),
            Candidate(2, "A2", ["Elves"]),
            Candidate(3, "A3", ["Elves", "Humans"]),
            Candidate(4, "A4", ["Elves", "Humans"]),
            Candidate(5, "A5", ["Elves", "Humans"]),
            Candidate(6, "B1", ["Humans"]),
            Candidate(7, "B2", ["Humans"]),
            Candidate(8, "B3", ["Humans"]),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        // Eight distinct athletes can field both teams despite the shared athletes.
        result.Teams.Count.ShouldBe(2);
        foreach (TypeCupAllocation.AllocatedTeam team in result.Teams)
        {
            team.Members.Count.ShouldBe(4);
        }

        HashSet<int> assigned = result.Teams.SelectMany(t => t.Members).Select(m => m.AthleteId).ToHashSet();
        assigned.Count.ShouldBe(8);
    }

    [Fact]
    public void OverlappingTypes_WithNonViableDistractor_MaximizesTwoTeams()
    {
        RulesV1 rules = Rules();

        // Aaa never reaches four and cannot participate, but N07 ranks #1 in both
        // Aaa and Bbb and therefore initially prefers the non-viable Aaa. A greedy
        // most-constrained-first allocation commits Bbb without N07 (all other Bbb
        // candidates prefer Bbb) and strands Ccc with only three remaining athletes.
        // The exact global allocation backtracks, assigns N07 to its only viable
        // type Bbb, and fields both Bbb and Ccc.
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "N00", ["Bbb"], bonus: 502),
            Candidate(2, "N01", ["Ccc"], bonus: 401),
            Candidate(3, "N02", ["Aaa"], bonus: 589),
            Candidate(4, "N03", ["Ccc"], bonus: 466),
            Candidate(5, "N04", ["Bbb", "Ccc"], bonus: 274),
            Candidate(6, "N05", ["Ccc"], bonus: 818),
            Candidate(7, "N06", ["Ccc", "Bbb"], bonus: 278),
            Candidate(8, "N07", ["Aaa", "Bbb"], bonus: 664),
            Candidate(9, "N08", ["Bbb"], bonus: 352),
            Candidate(10, "N09", ["Aaa"], bonus: 521),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        result.Teams.Count.ShouldBe(2);
        foreach (TypeCupAllocation.AllocatedTeam team in result.Teams)
        {
            team.Members.Count.ShouldBe(4);
        }

        HashSet<int> assigned = result.Teams.SelectMany(t => t.Members).Select(m => m.AthleteId).ToHashSet();
        assigned.Count.ShouldBe(8);
        assigned.Contains(8).ShouldBeTrue();

        TypeCupAllocation.AllocatedTeam bbb = result.Teams.Single(t => string.Equals(t.CreatureType, "Bbb", StringComparison.Ordinal));
        bbb.Members.Select(m => m.Name).Contains("N07", StringComparer.Ordinal).ShouldBeTrue();
        result.Teams.Any(t => string.Equals(t.CreatureType, "Aaa", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Fact]
    public void TypesWithFewerThanFour_NeverParticipate()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "G1", ["Goblin"]),
            Candidate(2, "G2", ["Goblin"]),
            Candidate(3, "G3", ["Goblin"]),
            Candidate(4, "E1", ["Elf"]),
            Candidate(5, "E2", ["Elf"]),
            Candidate(6, "E3", ["Elf"]),
            Candidate(7, "E4", ["Elf"]),
            Candidate(8, "E5", ["Elf"]),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        result.Teams.Count.ShouldBe(1);
        result.Teams[0].CreatureType.ShouldBe("Elf");
        result.Teams[0].Members.Count.ShouldBe(4);
    }

    [Fact]
    public void Allocation_IsDeterministic_AndUsesFixedPointScores()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "N1", ["Human"], bonus: 200, performance: 300, form: 400, prestige: 50),
            Candidate(2, "N2", ["Human"], bonus: 100, performance: 100, form: 100, prestige: 10),
            Candidate(3, "N3", ["Human"], bonus: 150, performance: 150, form: 150, prestige: 20),
            Candidate(4, "N4", ["Human"], bonus: 120, performance: 120, form: 120, prestige: 15),
            Candidate(5, "N5", ["Human"], bonus: 110, performance: 110, form: 110, prestige: 12),
        };

        TypeCupAllocation.AllocationResult first = TypeCupAllocation.Allocate(candidates, rules);
        TypeCupAllocation.AllocationResult second = TypeCupAllocation.Allocate(candidates, rules);

        first.Teams.Count.ShouldBe(1);
        TypeCupAllocation.AllocatedTeam team = first.Teams[0];
        team.Members.Count.ShouldBe(4);
        team.Members.Select(m => m.SelectionRank).OrderBy(r => r).ShouldBe([1, 2, 3, 4]);
        for (int i = 1; i < team.Members.Count; i++)
        {
            (team.Members[i].FinalRatingThousandths <= team.Members[i - 1].FinalRatingThousandths).ShouldBeTrue();
        }

        foreach (TypeCupAllocation.AllocatedMember member in team.Members)
        {
            int expected = SelectionScore.Combine(
                member.BonusNormThousandths,
                member.PerformanceNormThousandths,
                member.FormNormThousandths,
                member.PrestigeNormThousandths,
                rules.CupBonusWeightPermille,
                rules.CupPerformanceWeightPermille,
                rules.CupFormWeightPermille,
                rules.CupPrestigeWeightPermille).Thousandths;
            member.FinalRatingThousandths.ShouldBe(expected);
            member.TypeRank.ShouldBeGreaterThanOrEqualTo(1);
        }

        second.Teams[0].Members.Select(m => m.AthleteId)
            .ShouldBe(first.Teams[0].Members.Select(m => m.AthleteId));
    }

    [Fact]
    public void Preview_DoesNotCap_AthletesRemainUncapped()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "P1", ["Human", "Wizard"]),
            Candidate(2, "P2", ["Human", "Wizard"]),
            Candidate(3, "P3", ["Human", "Wizard"]),
            Candidate(4, "P4", ["Human", "Wizard"]),
        };

        // The pure kernel allocation carries no nationality side effects:
        // inputs are uncapped and outputs allocate without capping.
        foreach (TypeCupAllocation.CandidateRaw candidate in candidates)
        {
            candidate.CappedNationality.ShouldBeNull();
        }

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);
        result.Teams.Count.ShouldBe(1);

        foreach (TypeCupAllocation.CandidateRaw candidate in candidates)
        {
            candidate.CappedNationality.ShouldBeNull();
        }
    }

    [Fact]
    public void Insight_MatchesAllocation_AndExplainsEveryViableType()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "Star", ["Human", "Wizard"], bonus: 500),
            Candidate(2, "HumanA", ["Human"], bonus: 900),
            Candidate(3, "HumanB", ["Human"], bonus: 800),
            Candidate(4, "HumanC", ["Human"], bonus: 700),
            Candidate(5, "HumanD", ["Human"], bonus: 600),
            Candidate(6, "WizardB", ["Wizard"], bonus: 100),
            Candidate(7, "WizardC", ["Wizard"], bonus: 100),
            Candidate(8, "WizardD", ["Wizard"], bonus: 100),
            Candidate(9, "Loner", ["Sphinx"], bonus: 1000),
        };

        TypeCupAllocation.AllocationResult expected = TypeCupAllocation.Allocate(candidates, rules);
        TypeCupAllocationInsight.Result insight = TypeCupAllocationInsight.Allocate(candidates, rules);

        insight.CandidateCount.ShouldBe(9);
        insight.Allocation.Teams.Select(t => t.CreatureType).ShouldBe(expected.Teams.Select(t => t.CreatureType));
        for (int i = 0; i < expected.Teams.Count; i++)
        {
            insight.Allocation.Teams[i].Members.ShouldBe(expected.Teams[i].Members);
        }

        // Sphinx cannot field four athletes, so it has no standing at all.
        insight.Types.Select(t => t.CreatureType).ShouldBe(["Human", "Wizard"]);
        insight.Types.ShouldAllBe(t => t.FieldsTeam);

        TypeCupAllocationInsight.TypeStanding human = insight.Types[0];
        human.Ranking.Select(r => r.TypeRank).ShouldBe([1, 2, 3, 4, 5]);
        human.Ranking.Select(r => r.Candidate.Name).ShouldBe(["HumanA", "HumanB", "HumanC", "HumanD", "Star"]);
        human.Ranking[4].AssignedType.ShouldBe("Wizard");

        TypeCupAllocationInsight.TypeStanding wizard = insight.Types[1];
        wizard.Ranking[0].Candidate.Name.ShouldBe("Star");
        wizard.Ranking.ShouldAllBe(r => string.Equals(r.AssignedType, "Wizard", StringComparison.Ordinal));
    }

    [Fact]
    public void Insight_ReportsViableTypeThatLostItsAthletes()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "Athlete A", ["Human", "Wizard"]),
            Candidate(2, "Athlete B", ["Human", "Wizard"]),
            Candidate(3, "Athlete C", ["Human", "Wizard"]),
            Candidate(4, "Athlete D", ["Human", "Wizard"]),
        };

        TypeCupAllocationInsight.Result insight = TypeCupAllocationInsight.Allocate(candidates, rules);

        insight.Allocation.Teams.Select(t => t.CreatureType).ShouldBe(["Human"]);
        insight.Types.Single(t => string.Equals(t.CreatureType, "Human", StringComparison.Ordinal)).FieldsTeam.ShouldBeTrue();
        TypeCupAllocationInsight.TypeStanding wizard = insight.Types.Single(t => string.Equals(t.CreatureType, "Wizard", StringComparison.Ordinal));
        wizard.FieldsTeam.ShouldBeFalse();
        wizard.Ranking.ShouldAllBe(r => string.Equals(r.AssignedType, "Human", StringComparison.Ordinal));
    }

    [Fact]
    public void EligibleTypesFor_CappedReturnsOnlyNationality()
    {
        var capped = Candidate(1, "Capped", ["Human", "Wizard"], capped: "Wizard");
        TypeCupAllocation.EligibleTypesFor(capped).ShouldBe(["Wizard"]);

        var uncapped = Candidate(2, "Open", ["Wizard", "Human", "Human", " "]);
        TypeCupAllocation.EligibleTypesFor(uncapped).ShouldBe(["Human", "Wizard"]);
    }

    [Fact]
    public void WeakCappedDoNotLockSquad_StrongestLegalFourWin()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "WCapA", ["Wizard"], capped: "Wizard", bonus: 100),
            Candidate(2, "WCapB", ["Wizard"], capped: "Wizard", bonus: 90),
            Candidate(3, "WCapC", ["Wizard"], capped: "Wizard", bonus: 80),
            Candidate(4, "WCapD", ["Wizard"], capped: "Wizard", bonus: 70),
            Candidate(5, "StrongOne", ["Wizard", "Elf"], bonus: 900),
            Candidate(6, "StrongTwo", ["Wizard", "Elf"], bonus: 800),
            Candidate(7, "ElfA", ["Elf"], bonus: 200),
            Candidate(8, "ElfB", ["Elf"], bonus: 190),
            Candidate(9, "ElfC", ["Elf"], bonus: 180),
            Candidate(10, "ElfD", ["Elf"], bonus: 170),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        // Maximum cardinality is preserved: both types still field.
        result.Teams.Count.ShouldBe(2);
        TypeCupAllocation.AllocatedTeam wizard = result.Teams.Single(t => string.Equals(t.CreatureType, "Wizard", StringComparison.Ordinal));
        TypeCupAllocation.AllocatedTeam elf = result.Teams.Single(t => string.Equals(t.CreatureType, "Elf", StringComparison.Ordinal));
        wizard.Members.Count.ShouldBe(4);
        elf.Members.Count.ShouldBe(4);

        // The old CollectGuaranteedTeams shortcut froze Wizard to the four
        // exclusive capped athletes. The corrected allocator must pick the
        // strongest legal four: both shared strong athletes plus the two
        // strongest capped athletes.
        wizard.Members.Select(m => m.Name).Contains("StrongOne", StringComparer.Ordinal).ShouldBeTrue();
        wizard.Members.Select(m => m.Name).Contains("StrongTwo", StringComparer.Ordinal).ShouldBeTrue();
        wizard.Members.Select(m => m.TypeRank).OrderBy(r => r).ShouldBe([1, 2, 3, 4]);

        // The two weakest capped athletes are benched instead of locking the squad.
        result.UnassignedAthleteIds.Count.ShouldBe(2);
        result.UnassignedAthleteIds.ShouldContain(3);
        result.UnassignedAthleteIds.ShouldContain(4);
        result.UnassignedAthleteIds.ShouldNotContain(5);
        result.UnassignedAthleteIds.ShouldNotContain(6);
    }

    [Fact]
    public void CappedStatusGrantsNoPriority_WeakerCappedLosesToStrongerOpen()
    {
        RulesV1 rules = Rules();

        // No type has four exclusives, so the old guaranteed shortcut does not
        // trigger: this reaches the global min-cost assignment path. The old
        // 1_000_000_000 uncapped penalty kept the weak capped athletes and
        // benched a stronger open athlete; the corrected objective benches the
        // weakest rating regardless of cap status.
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "WCapWeak", ["Wizard"], capped: "Wizard", bonus: 70),
            Candidate(2, "WCapMid", ["Wizard"], capped: "Wizard", bonus: 90),
            Candidate(3, "WOpen", ["Wizard"], bonus: 100),
            Candidate(4, "SharedOne", ["Wizard", "Elf"], bonus: 900),
            Candidate(5, "SharedTwo", ["Wizard", "Elf"], bonus: 800),
            Candidate(6, "SharedThree", ["Wizard", "Elf"], bonus: 700),
            Candidate(7, "ElfA", ["Elf"], bonus: 110),
            Candidate(8, "ElfB", ["Elf"], bonus: 105),
            Candidate(9, "ElfC", ["Elf"], bonus: 95),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        result.Teams.Count.ShouldBe(2);
        HashSet<int> assigned = result.Teams.SelectMany(t => t.Members).Select(m => m.AthleteId).ToHashSet();
        assigned.Count.ShouldBe(8);
        result.UnassignedAthleteIds.Count.ShouldBe(1);

        // Weakest rating overall is the 70-bonus capped Wizard: it must be the
        // athlete left out. Capped status alone must not save it while a
        // stronger open athlete (95-bonus ElfC) takes a legal squad slot.
        result.UnassignedAthleteIds.Single().ShouldBe(1);
        assigned.ShouldContain(9);
        assigned.ShouldContain(4);
        assigned.ShouldContain(5);
        assigned.ShouldContain(6);
    }

    [Fact]
    public void StrongDualTypeAthleteIsNotStranded_MockeryPattern()
    {
        RulesV1 rules = Rules();

        // Mockery of Nature pattern: strong uncapped Eldrazi/Beast, #1 Eldrazi
        // (900 vs 100/90/80/70) and #2 Beast (950 BeastStrong is #1, Mockery 900
        // is #2). Both sides already have four exclusive athletes, so the old
        // shortcut stranded Mockery on neither team.
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "ECapA", ["Eldrazi"], capped: "Eldrazi", bonus: 100),
            Candidate(2, "ECapB", ["Eldrazi"], capped: "Eldrazi", bonus: 90),
            Candidate(3, "ECapC", ["Eldrazi"], capped: "Eldrazi", bonus: 80),
            Candidate(4, "ECapD", ["Eldrazi"], capped: "Eldrazi", bonus: 70),
            Candidate(5, "Mockery", ["Eldrazi", "Beast"], bonus: 900),
            Candidate(6, "BCapA", ["Beast"], capped: "Beast", bonus: 110),
            Candidate(7, "BCapB", ["Beast"], capped: "Beast", bonus: 95),
            Candidate(8, "BCapC", ["Beast"], capped: "Beast", bonus: 85),
            Candidate(9, "BCapD", ["Beast"], capped: "Beast", bonus: 75),
            Candidate(10, "BeastStrong", ["Beast"], bonus: 950),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        // Maximum cardinality is preserved: both types still field.
        result.Teams.Count.ShouldBe(2);
        foreach (TypeCupAllocation.AllocatedTeam team in result.Teams)
        {
            team.Members.Count.ShouldBe(4);
        }

        // Mockery is selected by exactly one eligible team, never zero or twice.
        List<TypeCupAllocation.AllocatedTeam> withMockery = result.Teams
            .Where(t => t.Members.Any(m => string.Equals(m.Name, "Mockery", StringComparison.Ordinal)))
            .ToList();
        withMockery.Count.ShouldBe(1);
        result.UnassignedAthleteIds.ShouldNotContain(5);

        // The roster made room for Mockery by benching weak capped athletes.
        result.UnassignedAthleteIds.Count.ShouldBe(2);
    }

    [Fact]
    public void PreferenceResolvesConflictWithoutBenchingStronger()
    {
        RulesV1 rules = Rules();

        // Shared athlete S ranks #1 Aaa (600 vs 500/400/300/200) but only #2 Bbb
        // (700 BeastStrong is #1). Both types already have four exclusives, so the
        // old shortcut stranded S. Totals are tied (displacing either 200-bonus
        // weakest gains the same rating), so relative preference must place S on
        // Aaa without benching it.
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "AaaA", ["Aaa"], bonus: 500),
            Candidate(2, "AaaB", ["Aaa"], bonus: 400),
            Candidate(3, "AaaC", ["Aaa"], bonus: 300),
            Candidate(4, "AaaD", ["Aaa"], bonus: 200),
            Candidate(5, "BbbStrong", ["Bbb"], bonus: 700),
            Candidate(6, "BbbB", ["Bbb"], bonus: 400),
            Candidate(7, "BbbC", ["Bbb"], bonus: 300),
            Candidate(8, "BbbD", ["Bbb"], bonus: 200),
            Candidate(9, "Swing", ["Aaa", "Bbb"], bonus: 600),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        result.Teams.Count.ShouldBe(2);
        TypeCupAllocation.AllocatedTeam aaa = result.Teams.Single(t => string.Equals(t.CreatureType, "Aaa", StringComparison.Ordinal));
        TypeCupAllocation.AllocatedTeam bbb = result.Teams.Single(t => string.Equals(t.CreatureType, "Bbb", StringComparison.Ordinal));

        // Preference places Swing where it ranks higher.
        aaa.Members.Select(m => m.Name).Contains("Swing", StringComparer.Ordinal).ShouldBeTrue();
        bbb.Members.Select(m => m.Name).Contains("Swing", StringComparer.Ordinal).ShouldBeFalse();

        // Swing is not benched merely for preferring Aaa: a legal squad slot
        // exists and the maximum team count is preserved.
        result.UnassignedAthleteIds.Count.ShouldBe(1);
        result.UnassignedAthleteIds.ShouldNotContain(9);
    }

    [Fact]
    public void CappedAthleteWithMultiplePrintedTypes_NeverRepresentsOtherType()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "CappedStar", ["Human", "Wizard"], capped: "Wizard", bonus: 900),
            Candidate(2, "WizardA", ["Wizard"], bonus: 800),
            Candidate(3, "WizardB", ["Wizard"], bonus: 700),
            Candidate(4, "WizardC", ["Wizard"], bonus: 600),
            Candidate(5, "HumanA", ["Human"], bonus: 500),
            Candidate(6, "HumanB", ["Human"], bonus: 400),
            Candidate(7, "HumanC", ["Human"], bonus: 300),
            Candidate(8, "HumanD", ["Human"], bonus: 200),
        };

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);

        result.Teams.Count.ShouldBe(2);
        TypeCupAllocation.AllocatedTeam wizard = result.Teams.Single(t => string.Equals(t.CreatureType, "Wizard", StringComparison.Ordinal));
        TypeCupAllocation.AllocatedTeam human = result.Teams.Single(t => string.Equals(t.CreatureType, "Human", StringComparison.Ordinal));
        wizard.Members.Select(m => m.Name).Contains("CappedStar", StringComparer.Ordinal).ShouldBeTrue();
        human.Members.Select(m => m.Name).Contains("CappedStar", StringComparer.Ordinal).ShouldBeFalse();
        TypeCupAllocation.EligibleTypesFor(candidates[0]).ShouldBe(["Wizard"]);
    }

    [Fact]
    public void SelectionInsight_MatchesCorrectedStrongestSquad()
    {
        RulesV1 rules = Rules();
        var candidates = new List<TypeCupAllocation.CandidateRaw>
        {
            Candidate(1, "WCapA", ["Wizard"], capped: "Wizard", bonus: 100),
            Candidate(2, "WCapB", ["Wizard"], capped: "Wizard", bonus: 90),
            Candidate(3, "WCapC", ["Wizard"], capped: "Wizard", bonus: 80),
            Candidate(4, "WCapD", ["Wizard"], capped: "Wizard", bonus: 70),
            Candidate(5, "StrongOne", ["Wizard", "Elf"], bonus: 900),
            Candidate(6, "StrongTwo", ["Wizard", "Elf"], bonus: 800),
            Candidate(7, "ElfA", ["Elf"], bonus: 200),
            Candidate(8, "ElfB", ["Elf"], bonus: 190),
            Candidate(9, "ElfC", ["Elf"], bonus: 180),
            Candidate(10, "ElfD", ["Elf"], bonus: 170),
        };

        TypeCupAllocation.AllocationResult expected = TypeCupAllocation.Allocate(candidates, rules);
        TypeCupAllocationInsight.Result insight = TypeCupAllocationInsight.Allocate(candidates, rules);

        insight.Allocation.Teams.Select(t => t.CreatureType).ShouldBe(expected.Teams.Select(t => t.CreatureType));
        for (int i = 0; i < expected.Teams.Count; i++)
        {
            insight.Allocation.Teams[i].Members.ShouldBe(expected.Teams[i].Members);
        }

        // The Wizard standing must show the corrected top-four selected: ranks
        // 1..4 selected, ranks 5..6 not placed. No weaker capped athlete is
        // selected ahead of a higher-ranked eligible athlete for the same team.
        TypeCupAllocationInsight.TypeStanding wizard = insight.Types.Single(t => string.Equals(t.CreatureType, "Wizard", StringComparison.Ordinal));
        wizard.FieldsTeam.ShouldBeTrue();
        wizard.Ranking.Count.ShouldBe(6);
        for (int i = 0; i < 4; i++)
        {
            wizard.Ranking[i].AssignedType.ShouldBe("Wizard");
        }

        wizard.Ranking[4].AssignedType.ShouldBeNull();
        wizard.Ranking[5].AssignedType.ShouldBeNull();
    }
}
