using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

/// <summary>
/// MSS-066 deterministic cross-tier regression scenarios (normal CI).
/// Protects the intended league-strength hierarchy while allowing legitimate
/// lower-tier upsets. All scenarios are pure kernel, integer-only and
/// deterministic: no RNG, clock, database or wall-clock.
/// Outer weights stay 35/30/25/10 and strength factors stay 1000/800/600/400.
/// No tier quota or absolute tier-priority rule is introduced: ordering is by
/// final selection rating only, so an exceptional lower-tier season can beat a
/// weak higher-tier season, but an identical F3 season never equals an F1 one.
/// </summary>
public sealed class CupSelectionCrossTierRegressionTests
{
    private static RulesV3 TieredRules() => RulesV3.CreateDefault();

    private static List<ColorCupSelection.StageFormEntry> FinalWindow(int season, int pointsEach)
    {
        List<ColorCupSelection.StageFormEntry> stages = [];
        for (int stage = 23; stage <= 32; stage++)
        {
            stages.Add(new ColorCupSelection.StageFormEntry(season, stage, pointsEach));
        }

        return stages;
    }

    private static CupSelectionMetrics.CupMetrics MetricsFor(
        int unadjustedPerformance,
        int pointsPerStage,
        int season,
        LeagueLevel level,
        RulesV3 rules)
    {
        return CupSelectionMetrics.Build(unadjustedPerformance, FinalWindow(season, pointsPerStage), season, level, rules);
    }

    private static ColorCupSelection.CandidateRaw ColorCandidate(
        int id,
        string name,
        int bonusRaw,
        CupSelectionMetrics.CupMetrics metrics,
        int prestigeRaw)
    {
        return new ColorCupSelection.CandidateRaw(
            id, name, 0, bonusRaw,
            metrics.AdjustedPerformanceThousandths, metrics.AdjustedFormRaw, prestigeRaw);
    }

    private static bool IsName(ColorCupSelection.ScoredCandidate candidate, string name)
    {
        return string.Equals(candidate.Name, name, StringComparison.Ordinal);
    }

    private static bool IsTypeName(TypeCupAllocation.AllocatedMember member, string name)
    {
        return string.Equals(member.Name, name, StringComparison.Ordinal);
    }

    private static void ShouldBeTopTwoLowerTier(IReadOnlyList<ColorCupSelection.ScoredCandidate> ranking)
    {
        string[] allowed = ["F2 Strong", "F3 Strong"];
        allowed.Any(n => string.Equals(ranking[0].Name, n, StringComparison.Ordinal)).ShouldBeTrue();
        allowed.Any(n => string.Equals(ranking[1].Name, n, StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public void OuterWeights_Remain_35_30_25_10()
    {
        RulesV3 rules = TieredRules();
        rules.CupBonusWeightPermille.ShouldBe(350);
        rules.CupPerformanceWeightPermille.ShouldBe(300);
        rules.CupFormWeightPermille.ShouldBe(250);
        rules.CupPrestigeWeightPermille.ShouldBe(100);
        checked
        {
            int total = rules.CupBonusWeightPermille + rules.CupPerformanceWeightPermille
                + rules.CupFormWeightPermille + rules.CupPrestigeWeightPermille;
            total.ShouldBe(1000);
        }
    }

    [Fact]
    public void StrengthFactors_Remain_1000_800_600_400()
    {
        RulesV3 rules = TieredRules();
        rules.GetCupStrengthFactor(LeagueLevel.Superleague).ShouldBe(1000);
        rules.GetCupStrengthFactor(LeagueLevel.Feeder1).ShouldBe(800);
        rules.GetCupStrengthFactor(LeagueLevel.Feeder2).ShouldBe(600);
        rules.GetCupStrengthFactor(LeagueLevel.Feeder3).ShouldBe(400);
    }

    [Fact]
    public void IdenticalSportingInputs_HigherTierRanksFirst()
    {
        RulesV3 rules = TieredRules();
        CupSelectionMetrics.CupMetrics super = MetricsFor(10_000, 1000, 1, LeagueLevel.Superleague, rules);
        CupSelectionMetrics.CupMetrics f1 = MetricsFor(10_000, 1000, 1, LeagueLevel.Feeder1, rules);
        CupSelectionMetrics.CupMetrics f2 = MetricsFor(10_000, 1000, 1, LeagueLevel.Feeder2, rules);
        CupSelectionMetrics.CupMetrics f3 = MetricsFor(10_000, 1000, 1, LeagueLevel.Feeder3, rules);

        super.AdjustedPerformanceThousandths.ShouldBe(10_000);
        f1.AdjustedPerformanceThousandths.ShouldBe(8_000);
        f2.AdjustedPerformanceThousandths.ShouldBe(6_000);
        f3.AdjustedPerformanceThousandths.ShouldBe(4_000);
        super.AdjustedFormRaw.ShouldBe(55_000);
        f1.AdjustedFormRaw.ShouldBe(44_000);
        f2.AdjustedFormRaw.ShouldBe(33_000);
        f3.AdjustedFormRaw.ShouldBe(22_000);

        List<ColorCupSelection.CandidateRaw> candidates =
        [
            ColorCandidate(1, "Super Star", 5000, super, 0),
            ColorCandidate(2, "F1 Grinder", 5000, f1, 0),
            ColorCandidate(3, "F2 Prospect", 5000, f2, 0),
            ColorCandidate(4, "F3 Rookie", 5000, f3, 0),
        ];
        IReadOnlyList<ColorCupSelection.ScoredCandidate> ranking = ColorCupSelection.RankAll(candidates, rules);
        string.Equals(ranking[0].Name, "Super Star", StringComparison.Ordinal).ShouldBeTrue();
        string.Equals(ranking[1].Name, "F1 Grinder", StringComparison.Ordinal).ShouldBeTrue();
        string.Equals(ranking[2].Name, "F2 Prospect", StringComparison.Ordinal).ShouldBeTrue();
        string.Equals(ranking[3].Name, "F3 Rookie", StringComparison.Ordinal).ShouldBeTrue();
        ranking[0].FinalRatingThousandths.ShouldBeGreaterThan(ranking[1].FinalRatingThousandths);
        ranking[1].FinalRatingThousandths.ShouldBeGreaterThan(ranking[2].FinalRatingThousandths);
        ranking[2].FinalRatingThousandths.ShouldBeGreaterThan(ranking[3].FinalRatingThousandths);
    }

    [Fact]
    public void F3Champion_IsNotEquivalent_ToF1Champion()
    {
        RulesV3 rules = TieredRules();
        CupSelectionMetrics.CupMetrics f1 = MetricsFor(10_000, 1000, 1, LeagueLevel.Feeder1, rules);
        CupSelectionMetrics.CupMetrics f3 = MetricsFor(10_000, 1000, 1, LeagueLevel.Feeder3, rules);

        // Same unadjusted championship season, different competition strength.
        f1.AdjustedPerformanceThousandths.ShouldBe(8_000);
        f3.AdjustedPerformanceThousandths.ShouldBe(4_000);
        f1.AdjustedFormRaw.ShouldBe(44_000);
        f3.AdjustedFormRaw.ShouldBe(22_000);

        List<ColorCupSelection.CandidateRaw> candidates =
        [
            ColorCandidate(1, "F1 Champ", 5000, f1, 0),
            ColorCandidate(2, "F3 Champ", 5000, f3, 0),
            // Anchors keep normalization bounded without changing the pairwise gap.
            new(3, "Anchor Low", 0, 5000, 4_000, 22_000, 0),
            new(4, "Anchor High", 0, 5000, 8_000, 44_000, 0),
        ];
        IReadOnlyList<ColorCupSelection.ScoredCandidate> ranking = ColorCupSelection.RankAll(candidates, rules);
        ColorCupSelection.ScoredCandidate f1Ranked = ranking.Single(c => IsName(c, "F1 Champ"));
        ColorCupSelection.ScoredCandidate f3Ranked = ranking.Single(c => IsName(c, "F3 Champ"));
        f1Ranked.PerformanceNormThousandths.ShouldBeGreaterThan(f3Ranked.PerformanceNormThousandths);
        f1Ranked.FormNormThousandths.ShouldBeGreaterThan(f3Ranked.FormNormThousandths);
        f1Ranked.FinalRatingThousandths.ShouldBeGreaterThan(f3Ranked.FinalRatingThousandths);
        f1Ranked.SelectionRank.ShouldBeLessThan(f3Ranked.SelectionRank);
    }

    [Fact]
    public void ExceptionalLowerTier_CanOutrank_WeakHigherTier_WithoutQuota()
    {
        RulesV3 rules = TieredRules();
        // Weak higher-tier seasons.
        CupSelectionMetrics.CupMetrics superWeak = MetricsFor(2_000, 100, 1, LeagueLevel.Superleague, rules);
        CupSelectionMetrics.CupMetrics f1Weak = MetricsFor(3_000, 100, 1, LeagueLevel.Feeder1, rules);
        // Exceptional lower-tier seasons: higher unadjusted output overcomes the factor.
        CupSelectionMetrics.CupMetrics f2Strong = MetricsFor(12_000, 1000, 1, LeagueLevel.Feeder2, rules);
        CupSelectionMetrics.CupMetrics f3Strong = MetricsFor(15_000, 1000, 1, LeagueLevel.Feeder3, rules);

        f2Strong.AdjustedPerformanceThousandths.ShouldBeGreaterThan(f1Weak.AdjustedPerformanceThousandths);
        f3Strong.AdjustedPerformanceThousandths.ShouldBeGreaterThan(superWeak.AdjustedPerformanceThousandths);

        List<ColorCupSelection.CandidateRaw> candidates =
        [
            ColorCandidate(1, "Super Weak", 1000, superWeak, 0),
            ColorCandidate(2, "F1 Weak", 1000, f1Weak, 0),
            ColorCandidate(3, "F2 Strong", 1000, f2Strong, 0),
            ColorCandidate(4, "F3 Strong", 1000, f3Strong, 0),
        ];
        IReadOnlyList<ColorCupSelection.ScoredCandidate> ranking = ColorCupSelection.RankAll(candidates, rules);
        ColorCupSelection.ScoredCandidate f3 = ranking.Single(c => IsName(c, "F3 Strong"));
        ColorCupSelection.ScoredCandidate super = ranking.Single(c => IsName(c, "Super Weak"));
        f3.SelectionRank.ShouldBeLessThan(super.SelectionRank);

        // No absolute tier priority: the top of the ranking is lower-tier.
        ShouldBeTopTwoLowerTier(ranking);

        // Color Cup takes the top four; with five candidates the weak Superleague
        // athlete is the one who misses the cut when a strong F3 athlete passes it.
        List<ColorCupSelection.CandidateRaw> five =
        [
            ColorCandidate(11, "Super Weak", 1000, superWeak, 0),
            ColorCandidate(12, "F1 Weak", 1000, f1Weak, 0),
            ColorCandidate(13, "F2 Strong", 1000, f2Strong, 0),
            ColorCandidate(14, "F3 Strong", 1000, f3Strong, 0),
            ColorCandidate(15, "F1 Mid", 1000, MetricsFor(6_000, 500, 1, LeagueLevel.Feeder1, rules), 0),
        ];
        IReadOnlyList<ColorCupSelection.ScoredCandidate> fiveRanking = ColorCupSelection.RankAll(five, rules);
        IReadOnlyList<ColorCupSelection.ScoredCandidate> team = ColorCupSelection.SelectTeam(five, rules);
        team.Any(c => IsName(c, "F3 Strong")).ShouldBeTrue();
        fiveRanking.Single(c => IsName(c, "F3 Strong")).SelectionRank.ShouldBeLessThanOrEqualTo(4);
        fiveRanking.Single(c => IsName(c, "Super Weak")).SelectionRank.ShouldBeGreaterThan(4);
    }

    [Fact]
    public void FormerSuperstar_InLowerTier_RemainsCompetitive_ViaBonusAndPrestige()
    {
        RulesV3 rules = TieredRules();
        Dictionary<int, LeagueLevel> levels = new()
        {
            [10] = LeagueLevel.Superleague,
            [11] = LeagueLevel.Feeder1,
            [12] = LeagueLevel.Feeder2,
            [13] = LeagueLevel.Feeder3,
        };
        // Former Superleague champion (kind 1) vs an F1 athlete with no prestige.
        CupPrestigeCalculator.PrestigeBreakdown starPrestige = CupPrestigeCalculator.ComputeForAthlete(
            1, [new(1, 1, 10)], [], [], levels, rules);
        CupPrestigeCalculator.PrestigeBreakdown grinderPrestige = CupPrestigeCalculator.ComputeForAthlete(
            2, [], [], [], levels, rules);
        starPrestige.TotalRaw.ShouldBe(1200);
        grinderPrestige.TotalRaw.ShouldBe(0);

        // Star now in F3 with a moderate season; grinder in F1 with a stronger
        // adjusted season. Bonus carries the tiered-generation advantage.
        // Anchors sit outside both athletes so the star keeps partial
        // performance/form credit: without them the star would sit at the field
        // minimum on both components and lose 450 to 550 despite max bonus and
        // prestige. The wider field shows bonus + prestige compensating a
        // moderate tiered gap rather than an extreme one.
        CupSelectionMetrics.CupMetrics starMetrics = MetricsFor(6_000, 600, 1, LeagueLevel.Feeder3, rules);
        CupSelectionMetrics.CupMetrics grinderMetrics = MetricsFor(8_000, 800, 1, LeagueLevel.Feeder1, rules);
        List<ColorCupSelection.CandidateRaw> candidates =
        [
            new(1, "Star F3", 0, 9000, starMetrics.AdjustedPerformanceThousandths, starMetrics.AdjustedFormRaw, starPrestige.TotalRaw),
            new(2, "Grinder F1", 0, 1000, grinderMetrics.AdjustedPerformanceThousandths, grinderMetrics.AdjustedFormRaw, grinderPrestige.TotalRaw),
            new(3, "Anchor Low", 0, 1000, 1_000, 5_000, 0),
            new(4, "Anchor High", 0, 9000, 8_000, 44_000, starPrestige.TotalRaw),
        ];
        IReadOnlyList<ColorCupSelection.ScoredCandidate> ranking = ColorCupSelection.RankAll(candidates, rules);
        ColorCupSelection.ScoredCandidate star = ranking.Single(c => IsName(c, "Star F3"));
        ColorCupSelection.ScoredCandidate grinder = ranking.Single(c => IsName(c, "Grinder F1"));
        // Star's max bonus + max prestige outweigh a moderate adjusted gap.
        star.SelectionRank.ShouldBeLessThan(grinder.SelectionRank);
    }

    [Fact]
    public void FormerSuperstar_BonusDecay_CanBeOutweighed_ByCurrentTierForm()
    {
        RulesV3 rules = TieredRules();
        // Same prestige as above, but the star's bonus has decayed to the minimum
        // while the higher-tier rival posts a clearly stronger current season.
        CupSelectionMetrics.CupMetrics starMetrics = MetricsFor(6_000, 600, 1, LeagueLevel.Feeder3, rules);
        CupSelectionMetrics.CupMetrics grinderMetrics = MetricsFor(8_000, 800, 1, LeagueLevel.Feeder1, rules);
        const int starPrestige = 1200;
        List<ColorCupSelection.CandidateRaw> candidates =
        [
            new(1, "Star Faded", 0, 1000, starMetrics.AdjustedPerformanceThousandths, starMetrics.AdjustedFormRaw, starPrestige),
            new(2, "Grinder F1", 0, 1000, grinderMetrics.AdjustedPerformanceThousandths, grinderMetrics.AdjustedFormRaw, 0),
            new(3, "Anchor Low", 0, 1000, starMetrics.AdjustedPerformanceThousandths, starMetrics.AdjustedFormRaw, 0),
            new(4, "Anchor High", 0, 9000, grinderMetrics.AdjustedPerformanceThousandths, grinderMetrics.AdjustedFormRaw, starPrestige),
        ];
        IReadOnlyList<ColorCupSelection.ScoredCandidate> ranking = ColorCupSelection.RankAll(candidates, rules);
        ColorCupSelection.ScoredCandidate star = ranking.Single(c => IsName(c, "Star Faded"));
        ColorCupSelection.ScoredCandidate grinder = ranking.Single(c => IsName(c, "Grinder F1"));
        // Bonus tied at the floor and prestige alone cannot cover the tiered
        // performance/form gap: prestige decays in influence without being deleted.
        grinder.SelectionRank.ShouldBeLessThan(star.SelectionRank);
    }

    [Fact]
    public void PoolAthlete_WithStaleHistory_HasNoRecentFormAdvantage()
    {
        RulesV3 rules = TieredRules();
        List<ColorCupSelection.StageFormEntry> staleHistory = [];
        for (int season = 1; season <= 3; season++)
        {
            staleHistory.AddRange(FinalWindow(season, 5000));
        }

        CupSelectionMetrics.CupMetrics pool = CupSelectionMetrics.Build(null, staleHistory, 3, null, rules);
        pool.AdjustedPerformanceThousandths.ShouldBe(0);
        pool.AdjustedFormRaw.ShouldBe(0);
        pool.StrengthFactorPermille.ShouldBe(0);

        CupSelectionMetrics.CupMetrics f3 = MetricsFor(4_000, 400, 1, LeagueLevel.Feeder3, rules);
        List<ColorCupSelection.CandidateRaw> candidates =
        [
            new(1, "Pool Veteran", 0, 1000, pool.AdjustedPerformanceThousandths, pool.AdjustedFormRaw, 0),
            ColorCandidate(2, "F3 Active", 1000, f3, 0),
            ColorCandidate(3, "F2 Active", 1000, MetricsFor(5_000, 500, 1, LeagueLevel.Feeder2, rules), 0),
            ColorCandidate(4, "F1 Active", 1000, MetricsFor(6_000, 600, 1, LeagueLevel.Feeder1, rules), 0),
        ];
        IReadOnlyList<ColorCupSelection.ScoredCandidate> ranking = ColorCupSelection.RankAll(candidates, rules);
        string.Equals(ranking[^1].Name, "Pool Veteran", StringComparison.Ordinal).ShouldBeTrue();
        ranking.Single(c => IsName(c, "F3 Active")).SelectionRank
            .ShouldBeLessThan(ranking.Single(c => IsName(c, "Pool Veteran")).SelectionRank);
    }

    [Fact]
    public void TypeCup_IdenticalInputs_HigherTierRanksFirst_And_UpsetRemainsPossible()
    {
        RulesV3 rules = TieredRules();
        CupSelectionMetrics.CupMetrics super = MetricsFor(10_000, 1000, 2, LeagueLevel.Superleague, rules);
        CupSelectionMetrics.CupMetrics f1 = MetricsFor(10_000, 1000, 2, LeagueLevel.Feeder1, rules);
        CupSelectionMetrics.CupMetrics f2 = MetricsFor(10_000, 1000, 2, LeagueLevel.Feeder2, rules);
        CupSelectionMetrics.CupMetrics f3 = MetricsFor(10_000, 1000, 2, LeagueLevel.Feeder3, rules);

        List<TypeCupAllocation.CandidateRaw> identical =
        [
            new(1, "Super Wizard", ["Wizard"], null, 1000, super.AdjustedPerformanceThousandths, super.AdjustedFormRaw, 0),
            new(2, "F1 Wizard", ["Wizard"], null, 1000, f1.AdjustedPerformanceThousandths, f1.AdjustedFormRaw, 0),
            new(3, "F2 Wizard", ["Wizard"], null, 1000, f2.AdjustedPerformanceThousandths, f2.AdjustedFormRaw, 0),
            new(4, "F3 Wizard", ["Wizard"], null, 1000, f3.AdjustedPerformanceThousandths, f3.AdjustedFormRaw, 0),
        ];
        TypeCupAllocation.AllocationResult hierarchy = TypeCupAllocation.Allocate(identical, rules);
        hierarchy.Teams.Count.ShouldBe(1);
        string.Equals(hierarchy.Teams[0].Members[0].Name, "Super Wizard", StringComparison.Ordinal).ShouldBeTrue();

        // Upset: exceptional F3 beats weak Superleague for the same type when the
        // unadjusted gap overcomes the factor, with no tier-priority override.
        CupSelectionMetrics.CupMetrics superWeak = MetricsFor(2_000, 100, 2, LeagueLevel.Superleague, rules);
        CupSelectionMetrics.CupMetrics f3Strong = MetricsFor(15_000, 1000, 2, LeagueLevel.Feeder3, rules);
        CupSelectionMetrics.CupMetrics f2Mid = MetricsFor(8_000, 800, 2, LeagueLevel.Feeder2, rules);
        List<TypeCupAllocation.CandidateRaw> upset =
        [
            new(11, "Super Weak", ["Elf"], null, 1000, superWeak.AdjustedPerformanceThousandths, superWeak.AdjustedFormRaw, 0),
            new(12, "F3 Strong", ["Elf"], null, 1000, f3Strong.AdjustedPerformanceThousandths, f3Strong.AdjustedFormRaw, 0),
            new(13, "F1 Mid A", ["Elf"], null, 1000, f1.AdjustedPerformanceThousandths, f1.AdjustedFormRaw, 0),
            new(14, "F1 Mid B", ["Elf"], null, 1000, f1.AdjustedPerformanceThousandths, f1.AdjustedFormRaw, 0),
            // Fifth candidate forces one athlete out: the weak higher-tier one.
            new(15, "F2 Mid", ["Elf"], null, 1000, f2Mid.AdjustedPerformanceThousandths, f2Mid.AdjustedFormRaw, 0),
        ];
        TypeCupAllocation.AllocationResult upsetResult = TypeCupAllocation.Allocate(upset, rules);
        upsetResult.Teams.Count.ShouldBe(1);
        upsetResult.Teams[0].Members.Any(m => IsTypeName(m, "F3 Strong")).ShouldBeTrue();
        upsetResult.UnassignedAthleteIds.ShouldContain(11);
    }

    [Fact]
    public void TypeCup_StalePoolLikeCandidate_RanksLast()
    {
        RulesV3 rules = TieredRules();
        CupSelectionMetrics.CupMetrics active = MetricsFor(6_000, 600, 2, LeagueLevel.Feeder1, rules);
        List<TypeCupAllocation.CandidateRaw> candidates =
        [
            new(21, "Stale Pool", ["Goblin"], null, 1000, 0, 0, 0),
            new(22, "Active A", ["Goblin"], null, 1000, active.AdjustedPerformanceThousandths, active.AdjustedFormRaw, 0),
            new(23, "Active B", ["Goblin"], null, 1000, active.AdjustedPerformanceThousandths, active.AdjustedFormRaw, 0),
            new(24, "Active C", ["Goblin"], null, 1000, active.AdjustedPerformanceThousandths, active.AdjustedFormRaw, 0),
            new(25, "Active D", ["Goblin"], null, 1000, active.AdjustedPerformanceThousandths, active.AdjustedFormRaw, 0),
        ];
        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);
        result.Teams.Count.ShouldBe(1);
        result.UnassignedAthleteIds.ShouldContain(21);
    }
}
