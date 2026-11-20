using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

/// <summary>
/// MSS-151 golden equivalence: pins the exact pre-refactor outputs of both Cup
/// selection paths that share the seven-field comparator (Type Cup allocation
/// and Color Cup selection). The values below were captured from the
/// pre-consolidation engines on this branch; they must stay unchanged and must
/// never be re-captured on failure. The candidates deliberately collide on
/// final rating, bonus, form, chaos and prestige components (including the
/// integer-division truncation case) so every comparator level is pinned.
/// </summary>
public sealed class CupSelectionGoldenEquivalenceTests
{
    private const string AllocationFingerprint =
        "Human=7:Eta Mindweaver:1:1:800:937:1000:294:269:1000;6:Zeta Ironfist:2:4:606:750:373:764:550:516;8:Theta Stonewall:3:5:490:375:447:882:157:408;10:Kappa Cappedheart:4:6:453:1000:74:58:1000:193"
        + "|Wizard=1:Alpha Wizardprime:1:1:802:875:970:529:101:946;3:Gamma Tieling:2:2:802:875:970:529:101:946;2:Beta Dualflame:3:4:795:1000:895:411:213:838;4:Delta Crossblade:4:5:638:625:671:647:325:731"
        + "|unassigned=5,9,13,12,11";

    private const string ColorRankedFingerprint =
        "1:5:Plain Winner:1000:1000:1000:1000:1000:1000"
        + "|2:3:Form Beats Chaos:4:6:0:10:0:0"
        + "|3:4:Chaos Behind Form:4:6:0:0:40:0"
        + "|4:7:Prestige Truncates:2:6:0:0:0:8"
        + "|5:1:Same Final A:2:6:0:0:0:0"
        + "|6:2:Same Final B:2:0:7:0:0:0";

    private const string ColorTeamFingerprint = "5:1,3:2,4:3,7:4";

    [Fact]
    public void TypeCupAllocation_MatchesPreRefactorGolden()
    {
        List<TypeCupAllocation.CandidateRaw> candidates =
        [
            new(1, "Alpha Wizardprime", ["Wizard"], null, 800_000, 700_000, 5_000, 1_000, 9_000),
            new(2, "Beta Dualflame", ["Wizard", "Human"], null, 900_000, 650_000, 4_000, 2_000, 8_000),
            new(3, "Gamma Tieling", ["Wizard"], null, 800_000, 700_000, 5_000, 1_000, 9_000),
            new(4, "Delta Crossblade", ["Wizard", "Human"], null, 600_000, 500_000, 6_000, 3_000, 7_000),
            new(5, "Epsilon Flarewing", ["Wizard"], null, 500_000, 400_000, 2_000, 4_000, 6_000),
            new(6, "Zeta Ironfist", ["Human"], null, 700_000, 300_000, 7_000, 5_000, 5_000),
            new(7, "Eta Mindweaver", ["Human", "Wizard"], null, 850_000, 720_000, 3_000, 2_500, 9_500),
            new(8, "Theta Stonewall", ["Human"], null, 400_000, 350_000, 8_000, 1_500, 4_000),
            new(9, "Iota Quickstep", ["Human"], null, 300_000, 250_000, 9_000, 3_500, 3_000),
            new(10, "Kappa Cappedheart", ["Human"], "Human", 900_000, 100_000, 1_000, 9_000, 2_000),
            new(11, "Lambda Goblinkin", ["Goblin"], null, 100_000, 50_000, 500, 100, 200),
            new(12, "Mu Ashfoot", ["Goblin"], null, 120_000, 60_000, 600, 120, 240),
            new(13, "Nu Skarsnik", ["Goblin"], null, 140_000, 70_000, 700, 140, 280),
        ];

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, RulesV1.CreateDefault());

        result.Teams.Count.ShouldBe(2);
        Fingerprint(result).ShouldBe(AllocationFingerprint);
    }

    [Fact]
    public void ColorCupSelection_MatchesPreRefactorGolden()
    {
        List<ColorCupSelection.CandidateRaw> candidates =
        [
            new(1, "Same Final A", 1, 6, 0, 0, 0, 0),
            new(2, "Same Final B", 1, 0, 7, 0, 0, 0),
            new(3, "Form Beats Chaos", 1, 6, 0, 10, 0, 0),
            new(4, "Chaos Behind Form", 1, 6, 0, 0, 40, 0),
            new(5, "Plain Winner", 1, 1000, 1000, 1000, 1000, 1000),
            new(7, "Prestige Truncates", 1, 6, 0, 0, 0, 8),
        ];

        IReadOnlyList<ColorCupSelection.ScoredCandidate> ranked =
            ColorCupSelection.RankAll(candidates, RulesV1.CreateDefault());
        IReadOnlyList<ColorCupSelection.ScoredCandidate> team =
            ColorCupSelection.SelectTeam(candidates, RulesV1.CreateDefault());

        ranked.Count.ShouldBe(6);
        string.Join("|", ranked.Select(c =>
            $"{c.SelectionRank}:{c.AthleteId}:{c.Name}:{c.FinalRatingThousandths}:{c.BonusNormThousandths}:{c.PerformanceNormThousandths}:{c.FormNormThousandths}:{c.ChaosNormThousandths}:{c.PrestigeNormThousandths}"))
            .ShouldBe(ColorRankedFingerprint);
        string.Join(",", team.Select(c => $"{c.AthleteId}:{c.SelectionRank}")).ShouldBe(ColorTeamFingerprint);
    }

    private static string Fingerprint(TypeCupAllocation.AllocationResult result)
    {
        List<string> teamParts = result.Teams.Select(t =>
        {
            string members = string.Join(";", t.Members.Select(m =>
                $"{m.AthleteId}:{m.Name}:{m.SelectionRank}:{m.TypeRank}:{m.FinalRatingThousandths}:{m.BonusNormThousandths}:{m.PerformanceNormThousandths}:{m.FormNormThousandths}:{m.ChaosNormThousandths}:{m.PrestigeNormThousandths}"));
            return $"{t.CreatureType}={members}";
        }).ToList();
        return string.Join("|", teamParts) + "|unassigned=" + string.Join(",", result.UnassignedAthleteIds);
    }
}
