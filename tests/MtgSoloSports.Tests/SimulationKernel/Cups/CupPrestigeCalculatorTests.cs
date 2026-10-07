using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

public sealed class CupPrestigeCalculatorTests
{
    private static RulesV3 TieredRules() => RulesV3.CreateDefault();

    private static RulesV1 LegacyRules() => RulesV1.CreateDefault();

    private static Dictionary<int, LeagueLevel> Levels(
        int superId,
        int f1Id,
        int f2Id,
        int f3Id) => new()
        {
            [superId] = LeagueLevel.Superleague,
            [f1Id] = LeagueLevel.Feeder1,
            [f2Id] = LeagueLevel.Feeder2,
            [f3Id] = LeagueLevel.Feeder3,
        };

    private static Dictionary<int, LeagueLevel> SingleLevel(int leagueId, LeagueLevel level) => new()
    {
        [leagueId] = level,
    };

    [Fact]
    public void Titles_Contribute_Super300_F1_100_F2_50_F3_25_Semantic()
    {
        RulesV3 rules = TieredRules();
        rules.PrestigeSuperTitlePoints.ShouldBe(1200);
        rules.PrestigeFeeder1TitlePoints.ShouldBe(400);
        rules.PrestigeFeeder2TitlePoints.ShouldBe(200);
        rules.PrestigeFeeder3TitlePoints.ShouldBe(100);

        var levels = Levels(10, 11, 12, 13);
        CupPrestigeCalculator.PrestigeBreakdown super = CupPrestigeCalculator.ComputeForAthlete(
            1,
            [new(1, 1, 10)],
            [],
            [],
            levels,
            rules);
        super.SuperTitleRaw.ShouldBe(1200);
        super.TotalRaw.ShouldBe(1200);

        CupPrestigeCalculator.PrestigeBreakdown f1 = CupPrestigeCalculator.ComputeForAthlete(
            2, [new(2, 0, 11)], [], [], levels, rules);
        f1.Feeder1TitleRaw.ShouldBe(400);

        CupPrestigeCalculator.PrestigeBreakdown f2 = CupPrestigeCalculator.ComputeForAthlete(
            3, [new(3, 0, 12)], [], [], levels, rules);
        f2.Feeder2TitleRaw.ShouldBe(200);

        CupPrestigeCalculator.PrestigeBreakdown f3 = CupPrestigeCalculator.ComputeForAthlete(
            4, [new(4, 0, 13)], [], [], levels, rules);
        f3.Feeder3TitleRaw.ShouldBe(100);
    }

    [Fact]
    public void OneSuperTitle_Equals_ThreeF1Titles()
    {
        RulesV3 rules = TieredRules();
        var levels = Levels(10, 11, 12, 13);
        CupPrestigeCalculator.PrestigeBreakdown super = CupPrestigeCalculator.ComputeForAthlete(
            1, [new(1, 1, 10)], [], [], levels, rules);
        CupPrestigeCalculator.PrestigeBreakdown triple = CupPrestigeCalculator.ComputeForAthlete(
            2, [new(2, 0, 11), new(2, 0, 11), new(2, 0, 11)], [], [], levels, rules);
        super.TotalRaw.ShouldBe(1200);
        triple.TotalRaw.ShouldBe(1200);
        triple.Feeder1TitleRaw.ShouldBe(1200);
    }

    [Fact]
    public void StagePodiums_Use_TwoX_OneX_HalfX_QuarterX_OfF1Baseline()
    {
        RulesV3 rules = TieredRules();
        rules.GetTieredStagePrestige(LeagueLevel.Superleague, 1).ShouldBe(80);
        rules.GetTieredStagePrestige(LeagueLevel.Superleague, 2).ShouldBe(40);
        rules.GetTieredStagePrestige(LeagueLevel.Superleague, 3).ShouldBe(16);
        rules.GetTieredStagePrestige(LeagueLevel.Feeder1, 1).ShouldBe(40);
        rules.GetTieredStagePrestige(LeagueLevel.Feeder1, 2).ShouldBe(20);
        rules.GetTieredStagePrestige(LeagueLevel.Feeder1, 3).ShouldBe(8);
        rules.GetTieredStagePrestige(LeagueLevel.Feeder2, 1).ShouldBe(20);
        rules.GetTieredStagePrestige(LeagueLevel.Feeder2, 2).ShouldBe(10);
        rules.GetTieredStagePrestige(LeagueLevel.Feeder2, 3).ShouldBe(4);
        rules.GetTieredStagePrestige(LeagueLevel.Feeder3, 1).ShouldBe(10);
        rules.GetTieredStagePrestige(LeagueLevel.Feeder3, 2).ShouldBe(5);
        rules.GetTieredStagePrestige(LeagueLevel.Feeder3, 3).ShouldBe(2);
    }

    [Fact]
    public void QuarterPoint_Exactness_For_F2F3_Second_Third()
    {
        // Semantic F2 2nd 2.5 x4 =10, 3rd 1 x4 =4; F3 2nd 1.25 x4 =5, 3rd 0.5 x4 =2.
        RulesV3 rules = TieredRules();
        rules.PrestigeFeeder2StageSecondPoints.ShouldBe(10);
        rules.PrestigeFeeder2StageThirdPoints.ShouldBe(4);
        rules.PrestigeFeeder3StageSecondPoints.ShouldBe(5);
        rules.PrestigeFeeder3StageThirdPoints.ShouldBe(2);
        CupPrestigeCalculator.PrestigeScale.ShouldBe(4);
        RulesV3.PrestigeScale.ShouldBe(4);
    }

    [Fact]
    public void SuperAppearance_Remains20Semantic_AndNoFeederAppearance()
    {
        RulesV3 rules = TieredRules();
        rules.PrestigeSuperAppearancePoints.ShouldBe(80);
        var levels = Levels(10, 11, 12, 13);

        CupPrestigeCalculator.PrestigeBreakdown two = CupPrestigeCalculator.ComputeForAthlete(
            1, [], [],
            [new(1, 10), new(1, 10)],
            levels, rules);
        two.AppearanceRaw.ShouldBe(160);
        two.TotalRaw.ShouldBe(160);

        CupPrestigeCalculator.PrestigeBreakdown feederOnly = CupPrestigeCalculator.ComputeForAthlete(
            2, [], [],
            [new(2, 11), new(2, 12), new(2, 13), new(2, null)],
            levels, rules);
        feederOnly.AppearanceRaw.ShouldBe(0);
        feederOnly.TotalRaw.ShouldBe(0);
    }

    [Fact]
    public void MajorCupTitles_Remain150Semantic_Each()
    {
        RulesV3 rules = TieredRules();
        rules.PrestigeMajorCupTitlePoints.ShouldBe(600);
        var levels = SingleLevel(10, LeagueLevel.Superleague);

        foreach (int kind in new[] { 2, 3, 4 })
        {
            CupPrestigeCalculator.PrestigeBreakdown single = CupPrestigeCalculator.ComputeForAthlete(
                7, [new(7, kind, 0)], [], [], levels, rules);
            single.MajorCupRaw.ShouldBe(600);
        }

        CupPrestigeCalculator.PrestigeBreakdown three = CupPrestigeCalculator.ComputeForAthlete(
            8, [new(8, 2, 0), new(8, 3, -1), new(8, 4, -3)], [], [], levels, rules);
        three.MajorCupRaw.ShouldBe(1800);
    }

    [Fact]
    public void RunnerUp_And_ThirdPlace_Add_No_Prestige()
    {
        RulesV3 rules = TieredRules();
        var levels = Levels(10, 11, 12, 13);
        List<CupPrestigeCalculator.PrestigeHonour> podiums =
        [
            new(1, 5, 11), new(1, 6, 11),
            new(1, 7, 10), new(1, 8, 10),
            new(1, 9, 0), new(1, 10, 0),
            new(1, 11, -1), new(1, 12, -1),
            new(1, 13, -3), new(1, 14, -3),
        ];
        CupPrestigeCalculator.PrestigeBreakdown result = CupPrestigeCalculator.ComputeForAthlete(
            1, podiums, [], [], levels, rules);
        result.TotalRaw.ShouldBe(0);
        result.MajorCupRaw.ShouldBe(0);
    }

    [Fact]
    public void HistoricalSuperstar_CurrentlyInF2_KeepsSuperPrestige()
    {
        RulesV3 rules = TieredRules();
        var levels = Levels(10, 11, 12, 13);

        // One Super title + two Super stage wins + one Super appearance,
        // earned in the Superleague; current source-season league is F2 (irrelevant).
        CupPrestigeCalculator.PrestigeBreakdown star = CupPrestigeCalculator.ComputeForAthlete(
            1,
            [new(1, 1, 10)],
            [new(1, 10, 1), new(1, 10, 1)],
            [new(1, 10)],
            levels,
            rules);
        star.SuperTitleRaw.ShouldBe(1200);
        star.SuperStageRaw.ShouldBe(160);
        star.AppearanceRaw.ShouldBe(80);
        star.TotalRaw.ShouldBe(1440);
    }

    [Fact]
    public void ManyLowerTier_Podiums_CanMatch_FewerUpperTier_Podiums()
    {
        RulesV3 rules = TieredRules();
        var levels = Levels(10, 11, 12, 13);

        // Eight F3 wins: 8 x 10 = 80 equals one Super win.
        CupPrestigeCalculator.PrestigeBreakdown many = CupPrestigeCalculator.ComputeForAthlete(
            1,
            [],
            [new(1, 13, 1), new(1, 13, 1), new(1, 13, 1), new(1, 13, 1),
             new(1, 13, 1), new(1, 13, 1), new(1, 13, 1), new(1, 13, 1)],
            [],
            levels,
            rules);
        CupPrestigeCalculator.PrestigeBreakdown one = CupPrestigeCalculator.ComputeForAthlete(
            2, [], [new(2, 10, 1)], [], levels, rules);
        many.Feeder3StageRaw.ShouldBe(80);
        one.SuperStageRaw.ShouldBe(80);
        many.TotalRaw.ShouldBe(one.TotalRaw);
    }

    [Fact]
    public void HistoricalV1Feeder_Maps_To_F1()
    {
        LeagueHierarchy.LevelForDivision(FeederDivision.None, isSuperleague: false).ShouldBe(LeagueLevel.Feeder1);
    }

    [Fact]
    public void UnknownLeague_ForTitleOrStageOrAppearance_Aborts()
    {
        RulesV3 rules = TieredRules();
        var levels = SingleLevel(10, LeagueLevel.Superleague);
        Should.Throw<InvalidOperationException>(() => CupPrestigeCalculator.ComputeForAthlete(
            1, [new(1, 0, 999)], [], [], levels, rules));
        Should.Throw<InvalidOperationException>(() => CupPrestigeCalculator.ComputeForAthlete(
            1, [], [new(1, 999, 1)], [], levels, rules));
        Should.Throw<InvalidOperationException>(() => CupPrestigeCalculator.ComputeForAthlete(
            1, [], [], [new(1, 999)], levels, rules));
    }

    [Fact]
    public void LegacyV1_Prestige_KeepsOriginalIntegers()
    {
        RulesV1 rules = LegacyRules();
        rules.Prestige.FeederTitlePoints.ShouldBe(100);
        rules.Prestige.SuperleagueTitlePoints.ShouldBe(300);
        rules.Prestige.SuperleagueAppearancePoints.ShouldBe(20);
        rules.Prestige.StageWinPoints.ShouldBe(10);
        rules.Prestige.StageSecondPoints.ShouldBe(5);
        rules.Prestige.StageThirdPoints.ShouldBe(2);
        rules.Prestige.OtherMajorHonourPoints.ShouldBe(150);

        var levels = Levels(10, 11, 12, 13);
        CupPrestigeCalculator.PrestigeBreakdown result = CupPrestigeCalculator.ComputeForAthlete(
            1,
            [new(1, 1, 10), new(1, 0, 11)],
            [new(1, 10, 1), new(1, 11, 2)],
            [new(1, 10)],
            levels,
            rules);
        result.SuperTitleRaw.ShouldBe(300);
        result.Feeder1TitleRaw.ShouldBe(100);
        result.SuperStageRaw.ShouldBe(10);
        result.Feeder1StageRaw.ShouldBe(5);
        result.AppearanceRaw.ShouldBe(20);
        result.TotalRaw.ShouldBe(435);
    }

    [Fact]
    public void V3Snapshot_RoundTrips_WithTieredPrestige()
    {
        RulesV3 rules = RulesV3.CreateDefault();
        string json = RulesSnapshotCodec.Encode(rules);
        RulesV1 decoded = RulesSnapshotCodec.Decode(json);
        RulesV3 tiered = decoded.ShouldBeOfType<RulesV3>();
        tiered.PrestigeSuperTitlePoints.ShouldBe(1200);
        tiered.PrestigeFeeder1TitlePoints.ShouldBe(400);
        tiered.PrestigeFeeder2TitlePoints.ShouldBe(200);
        tiered.PrestigeFeeder3TitlePoints.ShouldBe(100);
        tiered.PrestigeSuperAppearancePoints.ShouldBe(80);
        tiered.PrestigeMajorCupTitlePoints.ShouldBe(600);
        tiered.PrestigeSuperStageWinPoints.ShouldBe(80);
        tiered.PrestigeFeeder1StageWinPoints.ShouldBe(40);
        tiered.PrestigeFeeder2StageWinPoints.ShouldBe(20);
        tiered.PrestigeFeeder3StageWinPoints.ShouldBe(10);
        Should.NotThrow(() => tiered.Validate());
    }

    [Fact]
    public void V1_And_V2_Snapshots_RemainReadable()
    {
        RulesV1 v1 = RulesV1.CreateDefault();
        RulesV1 backV1 = RulesSnapshotCodec.Decode(RulesSnapshotCodec.Encode(v1));
        backV1.Version.ShouldBe(1);
        backV1.CupPrestigeFeederTitlePoints.ShouldBe(100);

        RulesV2 v2 = RulesV2.CreateDefault();
        RulesV1 backV2 = RulesSnapshotCodec.Decode(RulesSnapshotCodec.Encode(v2));
        backV2.ShouldBeOfType<RulesV2>().Version.ShouldBe(2);
    }

    [Fact]
    public void InvalidTieredPrestige_FailsValidation()
    {
        Should.Throw<InvalidOperationException>(() => RulesV3.Create(null, prestigeSuperTitlePoints: 999));
        Should.Throw<InvalidOperationException>(() => RulesV3.Create(null, prestigeFeeder2TitlePoints: 999));
        Should.Throw<InvalidOperationException>(() => RulesV3.Create(null, prestigeFeeder3StageThirdPoints: 999));
    }

    [Fact]
    public void ColorCupReport_V3_RoundTrips_WithPrestigeBreakdown_SummingToRaw()
    {
        var candidate = new ColorCupSelectionReportDocument.Candidate(
            7, 1, 900, 1000, 800, 600, 1000, 5000, 8000, 44000, 1420,
            "White League", (int)LeagueLevel.Feeder1, 800, 10_000, 55_000,
            0, 400, 200, 100, 80, 0, 40, 0, 0, 600);
        int sum = candidate.PrestigeSuperTitleRaw + candidate.PrestigeFeeder1TitleRaw
            + candidate.PrestigeFeeder2TitleRaw + candidate.PrestigeFeeder3TitleRaw
            + candidate.PrestigeAppearanceRaw + candidate.PrestigeSuperStageRaw
            + candidate.PrestigeFeeder1StageRaw + candidate.PrestigeFeeder2StageRaw
            + candidate.PrestigeFeeder3StageRaw + candidate.PrestigeMajorCupRaw;
        sum.ShouldBe(candidate.PrestigeRaw);
        var report = new ColorCupSelectionReportDocument(
            ColorCupSelectionReportDocument.PayloadVersion, 350, 300, 250, 100,
            [new ColorCupSelectionReportDocument.Team(0, 1, [candidate])]);
        ColorCupSelectionReportDocument restored = ColorCupSelectionReportDocument.FromStored(report.ToStored());
        restored.Version.ShouldBe(3);
        ColorCupSelectionReportDocument.Candidate back = restored.Teams[0].Ranking[0];
        int backSum = back.PrestigeSuperTitleRaw + back.PrestigeFeeder1TitleRaw
            + back.PrestigeFeeder2TitleRaw + back.PrestigeFeeder3TitleRaw
            + back.PrestigeAppearanceRaw + back.PrestigeSuperStageRaw
            + back.PrestigeFeeder1StageRaw + back.PrestigeFeeder2StageRaw
            + back.PrestigeFeeder3StageRaw + back.PrestigeMajorCupRaw;
        backSum.ShouldBe(back.PrestigeRaw);
    }

    [Fact]
    public void OuterPrestigeWeight_Stays10Percent()
    {
        RulesV3 rules = TieredRules();
        rules.CupBonusWeightPermille.ShouldBe(350);
        rules.CupPerformanceWeightPermille.ShouldBe(300);
        rules.CupFormWeightPermille.ShouldBe(250);
        rules.CupPrestigeWeightPermille.ShouldBe(100);
    }

    [Fact]
    public void ColorAndTypeCups_ProduceIdenticalPrestige_ForSameHistory()
    {
        RulesV3 rules = TieredRules();
        var levels = Levels(10, 11, 12, 13);
        List<CupPrestigeCalculator.PrestigeHonour> honours =
        [
            new(1, 1, 10), new(1, 0, 12), new(1, 4, -3),
        ];
        List<CupPrestigeCalculator.PrestigeStage> stages =
        [
            new(1, 10, 1), new(1, 12, 2), new(1, 13, 3),
        ];
        List<CupPrestigeCalculator.PrestigeMembership> memberships = [new(1, 10), new(1, 12)];

        // Both Cups share CupPrestigeCalculator; identical history yields identical raw.
        CupPrestigeCalculator.PrestigeBreakdown color = CupPrestigeCalculator.ComputeForAthlete(
            1, honours, stages, memberships, levels, rules);
        CupPrestigeCalculator.PrestigeBreakdown type = CupPrestigeCalculator.ComputeForAthlete(
            1, honours, stages, memberships, levels, rules);
        color.TotalRaw.ShouldBe(type.TotalRaw);
        color.TotalRaw.ShouldBe(1200 + 200 + 600 + 80 + 10 + 2 + 80);
        color.ValidateSum();
    }

    [Fact]
    public void TypeCupQualificationAlone_AddsNoPrestige()
    {
        RulesV3 rules = TieredRules();
        var levels = Levels(10, 11, 12, 13);

        // Qualification-group success persists no honour row, so prestige is unchanged.
        // Only the Final champion honour (kind 4) counts; runner-up/third (13/14) do not.
        CupPrestigeCalculator.PrestigeBreakdown none = CupPrestigeCalculator.ComputeForAthlete(
            1, [], [], [], levels, rules);
        none.TotalRaw.ShouldBe(0);

        CupPrestigeCalculator.PrestigeBreakdown finalChampion = CupPrestigeCalculator.ComputeForAthlete(
            1, [new(1, 4, -3)], [], [], levels, rules);
        finalChampion.MajorCupRaw.ShouldBe(600);
    }
}
