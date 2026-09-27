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
    public void EligibleTypesFor_CappedReturnsOnlyNationality()
    {
        var capped = Candidate(1, "Capped", ["Human", "Wizard"], capped: "Wizard");
        TypeCupAllocation.EligibleTypesFor(capped).ShouldBe(["Wizard"]);

        var uncapped = Candidate(2, "Open", ["Wizard", "Human", "Human", " "]);
        TypeCupAllocation.EligibleTypesFor(uncapped).ShouldBe(["Human", "Wizard"]);
    }
}
