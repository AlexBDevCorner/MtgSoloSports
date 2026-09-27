using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

public sealed class ColorCupSelectionTests
{
    [Fact]
    public void Normalize_TiedComponent_Returns1000()
    {
        ColorCupSelection.Normalize(500, 500, 500).ShouldBe(1000);
        ColorCupSelection.Normalize(0, 0, 0).ShouldBe(1000);
    }

    [Fact]
    public void Normalize_ScalesWithinColor()
    {
        ColorCupSelection.Normalize(0, 0, 1000).ShouldBe(0);
        ColorCupSelection.Normalize(1000, 0, 1000).ShouldBe(1000);
        ColorCupSelection.Normalize(500, 0, 1000).ShouldBe(500);
        ColorCupSelection.Normalize(960, 960, 1000).ShouldBe(0);
        ColorCupSelection.Normalize(1000, 960, 1000).ShouldBe(1000);
    }

    [Fact]
    public void ComputeFormRaw_UsesWeights1To10OldestToNewest()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<ColorCupSelection.StageFormEntry> stages = [];
        for (int stage = 1; stage <= 10; stage++)
        {
            stages.Add(new ColorCupSelection.StageFormEntry(1, stage, 1000));
        }

        // 1000 * (1+2+...+10) = 55_000.
        ColorCupSelection.ComputeFormRaw(stages, rules).ShouldBe(55_000);
    }

    [Fact]
    public void ComputeFormRaw_NewestAligned_WhenFewerThanTen()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<ColorCupSelection.StageFormEntry> stages =
        [
            new(1, 30, 1000),
            new(1, 31, 1000),
            new(1, 32, 1000),
        ];

        // Three stages occupy the newest weights 8, 9, 10.
        ColorCupSelection.ComputeFormRaw(stages, rules).ShouldBe(27_000);
    }

    [Fact]
    public void ComputeFormRaw_Empty_ReturnsZero()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        ColorCupSelection.ComputeFormRaw([], rules).ShouldBe(0);
    }

    [Fact]
    public void ComputeFormRaw_TakesMostRecentTen()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<ColorCupSelection.StageFormEntry> stages = [];
        for (int stage = 1; stage <= 12; stage++)
        {
            stages.Add(new ColorCupSelection.StageFormEntry(1, stage, stage * 1000));
        }

        // Most recent ten are stages 3..12 with weights 1..10.
        int expected = 0;
        for (int i = 0; i < 10; i++)
        {
            expected += (3 + i) * 1000 * (i + 1);
        }

        ColorCupSelection.ComputeFormRaw(stages, rules).ShouldBe(expected);
    }

    [Fact]
    public void SelectTeam_OrdersByFinal_ThenDeterministicTieBreak()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<ColorCupSelection.CandidateRaw> candidates =
        [
            new(1, "Bravo", 0, 500, 500, 500, 500),
            new(2, "Alpha", 0, 500, 500, 500, 500),
            new(3, "Charlie", 0, 0, 0, 0, 0),
            new(4, "Delta", 0, 0, 0, 0, 0),
            new(5, "Echo", 0, 0, 0, 0, 0),
        ];

        IReadOnlyList<ColorCupSelection.ScoredCandidate> team = ColorCupSelection.SelectTeam(candidates, rules);
        team.Count.ShouldBe(4);
        team[0].Name.ShouldBe("Alpha");
        team[0].SelectionRank.ShouldBe(1);
        team[1].Name.ShouldBe("Bravo");
        team[1].SelectionRank.ShouldBe(2);
        team[0].FinalRatingThousandths.ShouldBe(team[1].FinalRatingThousandths);
    }

    [Fact]
    public void RecentForm_FlipsTeam_ComparedWithRawBonusRanking()
    {
        RulesV1 rules = RulesV1.CreateDefault();

        // Bonus ranking top four: A, B, C, D. Performance and prestige are tied
        // so only bonus (35%) and form (25%) decide.
        List<ColorCupSelection.CandidateRaw> candidates =
        [
            new(1, "FormA", 0, 1000, 500, 0, 0),
            new(2, "FormB", 0, 990, 500, 0, 0),
            new(3, "FormC", 0, 980, 500, 0, 0),
            new(4, "FormD", 0, 970, 500, 0, 0),
            new(5, "FormE", 0, 960, 500, 10_000, 0),
        ];

        List<string> bonusOrder = candidates
            .OrderByDescending(c => c.BonusRawThousandths)
            .Take(4)
            .Select(c => c.Name)
            .ToList();
        bonusOrder.ShouldBe(["FormA", "FormB", "FormC", "FormD"]);

        IReadOnlyList<ColorCupSelection.ScoredCandidate> team = ColorCupSelection.SelectTeam(candidates, rules);
        List<string> selected = team.Select(t => t.Name).ToList();
        selected.Contains("FormE", StringComparer.Ordinal).ShouldBeTrue();
        selected.Contains("FormD", StringComparer.Ordinal).ShouldBeFalse();
        selected.ShouldBe(["FormA", "FormB", "FormE", "FormC"]);
    }

    [Fact]
    public void CareerPrestige_FlipsTeam_ComparedWithRawBonusRanking()
    {
        RulesV1 rules = RulesV1.CreateDefault();

        // Bonus ranking top four: A, B, C, D. Performance and form are tied so
        // only bonus (35%) and prestige (10%) decide. Prestige flips the fourth slot.
        List<ColorCupSelection.CandidateRaw> candidates =
        [
            new(1, "PrestigeA", 0, 1000, 500, 500, 0),
            new(2, "PrestigeB", 0, 999, 500, 500, 0),
            new(3, "PrestigeC", 0, 998, 500, 500, 0),
            new(4, "PrestigeD", 0, 997, 500, 500, 0),
            new(5, "PrestigeE", 0, 996, 500, 500, 1000),
        ];

        List<string> bonusOrder = candidates
            .OrderByDescending(c => c.BonusRawThousandths)
            .Take(4)
            .Select(c => c.Name)
            .ToList();
        bonusOrder.ShouldBe(["PrestigeA", "PrestigeB", "PrestigeC", "PrestigeD"]);

        IReadOnlyList<ColorCupSelection.ScoredCandidate> team = ColorCupSelection.SelectTeam(candidates, rules);
        List<string> selected = team.Select(t => t.Name).ToList();
        selected.Contains("PrestigeE", StringComparer.Ordinal).ShouldBeTrue();
        selected.Contains("PrestigeD", StringComparer.Ordinal).ShouldBeFalse();
    }

    [Fact]
    public void PrestigeConstants_HaveDocumentedV1Values()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        rules.CupPrestigeFeederTitlePoints.ShouldBe(100);
        rules.CupPrestigeSuperleagueTitlePoints.ShouldBe(300);
        rules.CupPrestigeSuperleagueAppearancePoints.ShouldBe(20);
        rules.CupPrestigeStageWinPoints.ShouldBe(10);
        rules.CupPrestigeStageSecondPoints.ShouldBe(5);
        rules.CupPrestigeStageThirdPoints.ShouldBe(2);
        rules.CupPrestigeOtherMajorHonourPoints.ShouldBe(150);

        int raw = rules.Prestige.ComputeRaw(
            feederTitles: 1,
            superleagueTitles: 1,
            superleagueAppearances: 2,
            stageWins: 3,
            stageSeconds: 4,
            stageThirds: 5,
            otherMajorHonours: 1);
        raw.ShouldBe(100 + 300 + 40 + 30 + 20 + 10 + 150);
    }
}
