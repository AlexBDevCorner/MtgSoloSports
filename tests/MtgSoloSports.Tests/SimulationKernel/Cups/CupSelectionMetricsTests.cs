using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.SimulationKernel.Cups;

public sealed class CupSelectionMetricsTests
{
    private static RulesV2 TieredRules() => RulesV2.CreateDefault();

    private static List<ColorCupSelection.StageFormEntry> FinalWindow(int season, int pointsEach)
    {
        List<ColorCupSelection.StageFormEntry> stages = [];
        for (int stage = 23; stage <= 32; stage++)
        {
            stages.Add(new ColorCupSelection.StageFormEntry(season, stage, pointsEach));
        }

        return stages;
    }

    [Fact]
    public void GetStrengthFactor_ReturnsConfiguredPermille_ForAllFourLevels()
    {
        RulesV2 rules = TieredRules();
        CupSelectionMetrics.GetStrengthFactor(rules, LeagueLevel.Superleague).ShouldBe(1000);
        CupSelectionMetrics.GetStrengthFactor(rules, LeagueLevel.Feeder1).ShouldBe(800);
        CupSelectionMetrics.GetStrengthFactor(rules, LeagueLevel.Feeder2).ShouldBe(600);
        CupSelectionMetrics.GetStrengthFactor(rules, LeagueLevel.Feeder3).ShouldBe(400);
    }

    [Fact]
    public void V1_SupportsOnlySuperAndFeeder1_ForStrength()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        rules.GetCupStrengthFactor(LeagueLevel.Superleague).ShouldBe(1000);
        rules.GetCupStrengthFactor(LeagueLevel.Feeder1).ShouldBe(800);
        Should.Throw<InvalidOperationException>(() => rules.GetCupStrengthFactor(LeagueLevel.Feeder2));
        Should.Throw<InvalidOperationException>(() => rules.GetCupStrengthFactor(LeagueLevel.Feeder3));
    }

    [Fact]
    public void ScaleByStrength_TruncatesExplicitly()
    {
        CupSelectionMetrics.ScaleByStrength(10_000, 1000).ShouldBe(10_000);
        CupSelectionMetrics.ScaleByStrength(10_000, 800).ShouldBe(8_000);
        CupSelectionMetrics.ScaleByStrength(10_000, 600).ShouldBe(6_000);
        CupSelectionMetrics.ScaleByStrength(10_000, 400).ShouldBe(4_000);
        // Truncation: 1 * 800 / 1000 = 0, 3 * 600 / 1000 = 1, 1999 * 800 / 1000 = 1599.
        CupSelectionMetrics.ScaleByStrength(1, 800).ShouldBe(0);
        CupSelectionMetrics.ScaleByStrength(3, 600).ShouldBe(1);
        CupSelectionMetrics.ScaleByStrength(1999, 800).ShouldBe(1599);
    }

    [Fact]
    public void FinalWindowStageNumbers_GeneralizesFromSnapshot()
    {
        RulesV2 rules = TieredRules();
        CupSelectionMetrics.FinalWindowStageNumbers(rules).ShouldBe([23, 24, 25, 26, 27, 28, 29, 30, 31, 32]);
    }

    [Fact]
    public void IdenticalPerformance_AcrossFourLevels_ScalesAt100_80_60_40()
    {
        RulesV2 rules = TieredRules();
        List<ColorCupSelection.StageFormEntry> window = FinalWindow(1, 1000);
        CupSelectionMetrics.CupMetrics super = CupSelectionMetrics.Build(10_000, window, 1, LeagueLevel.Superleague, rules);
        CupSelectionMetrics.CupMetrics f1 = CupSelectionMetrics.Build(10_000, window, 1, LeagueLevel.Feeder1, rules);
        CupSelectionMetrics.CupMetrics f2 = CupSelectionMetrics.Build(10_000, window, 1, LeagueLevel.Feeder2, rules);
        CupSelectionMetrics.CupMetrics f3 = CupSelectionMetrics.Build(10_000, window, 1, LeagueLevel.Feeder3, rules);
        super.AdjustedPerformanceThousandths.ShouldBe(10_000);
        f1.AdjustedPerformanceThousandths.ShouldBe(8_000);
        f2.AdjustedPerformanceThousandths.ShouldBe(6_000);
        f3.AdjustedPerformanceThousandths.ShouldBe(4_000);
    }

    [Fact]
    public void IdenticalForm_AcrossFourLevels_ScalesAtConfiguredFactors()
    {
        RulesV2 rules = TieredRules();
        // Each stage 1000 points: unadjusted = 1000 * 55 = 55_000.
        CupSelectionMetrics.CupMetrics super = CupSelectionMetrics.Build(5000, FinalWindow(2, 1000), 2, LeagueLevel.Superleague, rules);
        CupSelectionMetrics.CupMetrics f1 = CupSelectionMetrics.Build(5000, FinalWindow(2, 1000), 2, LeagueLevel.Feeder1, rules);
        CupSelectionMetrics.CupMetrics f2 = CupSelectionMetrics.Build(5000, FinalWindow(2, 1000), 2, LeagueLevel.Feeder2, rules);
        CupSelectionMetrics.CupMetrics f3 = CupSelectionMetrics.Build(5000, FinalWindow(2, 1000), 2, LeagueLevel.Feeder3, rules);
        super.UnadjustedFormAggregate.ShouldBe(55_000);
        super.AdjustedFormRaw.ShouldBe(55_000);
        f1.AdjustedFormRaw.ShouldBe(44_000);
        f2.AdjustedFormRaw.ShouldBe(33_000);
        f3.AdjustedFormRaw.ShouldBe(22_000);
    }

    [Fact]
    public void StrongerLowerTier_CanExceed_WeakerHigherTier()
    {
        RulesV2 rules = TieredRules();
        // F2 10_000 -> 6_000 beats F1 5_000 -> 4_000.
        CupSelectionMetrics.CupMetrics f2 = CupSelectionMetrics.Build(10_000, FinalWindow(1, 1000), 1, LeagueLevel.Feeder2, rules);
        CupSelectionMetrics.CupMetrics f1 = CupSelectionMetrics.Build(5_000, FinalWindow(1, 1000), 1, LeagueLevel.Feeder1, rules);
        f2.AdjustedPerformanceThousandths.ShouldBeGreaterThan(f1.AdjustedPerformanceThousandths);
        // F3 10_000 -> 4_000 beats F2 5_000 -> 3_000.
        CupSelectionMetrics.CupMetrics f3 = CupSelectionMetrics.Build(10_000, FinalWindow(1, 1000), 1, LeagueLevel.Feeder3, rules);
        CupSelectionMetrics.CupMetrics weakF2 = CupSelectionMetrics.Build(5_000, FinalWindow(1, 1000), 1, LeagueLevel.Feeder2, rules);
        f3.AdjustedPerformanceThousandths.ShouldBeGreaterThan(weakF2.AdjustedPerformanceThousandths);
    }

    [Fact]
    public void PoolAthlete_WithExcellentHistory_GetsZeroPerformanceAndForm()
    {
        RulesV2 rules = TieredRules();
        List<ColorCupSelection.StageFormEntry> history = [];
        for (int season = 1; season <= 3; season++)
        {
            history.AddRange(FinalWindow(season, 5000));
        }

        CupSelectionMetrics.CupMetrics pool = CupSelectionMetrics.Build(null, history, 3, null, rules);
        pool.AdjustedPerformanceThousandths.ShouldBe(0);
        pool.AdjustedFormRaw.ShouldBe(0);
        pool.UnadjustedPerformanceThousandths.ShouldBe(0);
        pool.UnadjustedFormAggregate.ShouldBe(0);
        pool.StrengthFactorPermille.ShouldBe(0);
    }

    [Fact]
    public void PriorSeasonStages_DoNotEnter_CurrentForm()
    {
        RulesV2 rules = TieredRules();
        List<ColorCupSelection.StageFormEntry> all = [];
        // Season 1 excellent: 5000 per stage in final window.
        all.AddRange(FinalWindow(1, 5000));
        // Season 2 weak: 100 per stage in final window.
        all.AddRange(FinalWindow(2, 100));
        CupSelectionMetrics.CupMetrics metrics = CupSelectionMetrics.Build(9000, all, 2, LeagueLevel.Feeder1, rules);
        // Season 2 unadjusted form = 100 * 55 = 5_500, adjusted 0.8 = 4_400.
        metrics.UnadjustedFormAggregate.ShouldBe(5_500);
        metrics.AdjustedFormRaw.ShouldBe(4_400);
    }

    [Fact]
    public void MissingFinalWindowRow_FailsValidation()
    {
        RulesV2 rules = TieredRules();
        List<ColorCupSelection.StageFormEntry> incomplete = FinalWindow(1, 1000);
        incomplete.RemoveAt(0);
        Should.Throw<InvalidOperationException>(() =>
            CupSelectionMetrics.Build(5000, incomplete, 1, LeagueLevel.Superleague, rules));
    }

    [Fact]
    public void ActiveAthlete_WithoutStanding_FailsValidation()
    {
        RulesV2 rules = TieredRules();
        Should.Throw<InvalidOperationException>(() =>
            CupSelectionMetrics.Build(null, FinalWindow(1, 1000), 1, LeagueLevel.Feeder1, rules));
    }

    [Fact]
    public void V1Snapshot_RemainsReadable_AndUnchanged()
    {
        RulesV1 v1 = RulesV1.CreateDefault();
        string json = RulesSnapshotDocument.FromRules(v1).ToJson();
        RulesV1 decoded = RulesSnapshotCodec.Decode(json);
        decoded.Version.ShouldBe(1);
        decoded.GetCupStrengthFactor(LeagueLevel.Superleague).ShouldBe(1000);
        decoded.GetCupStrengthFactor(LeagueLevel.Feeder1).ShouldBe(800);
    }

    [Fact]
    public void V2Snapshot_Persists_AndValidates_StrengthFactors()
    {
        RulesV2 rules = TieredRules();
        string json = RulesSnapshotCodec.Encode(rules);
        RulesV1 decoded = RulesSnapshotCodec.Decode(json);
        RulesV2 tiered = decoded.ShouldBeOfType<RulesV2>();
        tiered.CupSuperleagueStrengthPermille.ShouldBe(1000);
        tiered.CupFeeder1StrengthPermille.ShouldBe(800);
        tiered.CupFeeder2StrengthPermille.ShouldBe(600);
        tiered.CupFeeder3StrengthPermille.ShouldBe(400);
        Should.NotThrow(() => tiered.Validate());
        Should.Throw<InvalidOperationException>(() => RulesV2.Create(null, cupFeeder1StrengthPermille: 700));
        Should.Throw<InvalidOperationException>(() => RulesV2.Create(null, cupSuperleagueStrengthPermille: 500, cupFeeder1StrengthPermille: 800, cupFeeder2StrengthPermille: 600, cupFeeder3StrengthPermille: 400));
    }

    [Fact]
    public void V2Snapshot_WithoutStrengthFields_DecodesWithDefaults()
    {
        // Simulate a v2 payload persisted before MSS-064 (zeroed strength fields).
        RulesV2 current = TieredRules();
        string json = RulesSnapshotCodec.Encode(current);
        string legacy = json
            .Replace("\"cupSuperleagueStrengthPermille\":1000", "\"cupSuperleagueStrengthPermille\":0")
            .Replace("\"cupFeeder1StrengthPermille\":800", "\"cupFeeder1StrengthPermille\":0")
            .Replace("\"cupFeeder2StrengthPermille\":600", "\"cupFeeder2StrengthPermille\":0")
            .Replace("\"cupFeeder3StrengthPermille\":400", "\"cupFeeder3StrengthPermille\":0");
        RulesV2 decoded = RulesSnapshotCodec.Decode(legacy).ShouldBeOfType<RulesV2>();
        decoded.CupSuperleagueStrengthPermille.ShouldBe(1000);
        decoded.CupFeeder1StrengthPermille.ShouldBe(800);
        decoded.CupFeeder2StrengthPermille.ShouldBe(600);
        decoded.CupFeeder3StrengthPermille.ShouldBe(400);
    }

    [Fact]
    public void ColorCupReport_V2_RoundTrips_WithAdjustedValues()
    {
        var candidate = new ColorCupSelectionReportDocument.Candidate(
            7, 1, 900, 1000, 800, 600, 1000, 5000, 8000, 44000, 300,
            "White League", (int)LeagueLevel.Feeder1, 800, 10_000, 55_000);
        var report = new ColorCupSelectionReportDocument(
            ColorCupSelectionReportDocument.PayloadVersion, 350, 300, 250, 100,
            [new ColorCupSelectionReportDocument.Team(0, 1, [candidate])]);
        ColorCupSelectionReportDocument restored = ColorCupSelectionReportDocument.FromStored(report.ToStored());
        restored.Version.ShouldBe(2);
        ColorCupSelectionReportDocument.Candidate back = restored.Teams[0].Ranking[0];
        back.SourceLeagueName.ShouldBe("White League");
        back.SourceLeagueLevel.ShouldBe((int)LeagueLevel.Feeder1);
        back.StrengthFactorPermille.ShouldBe(800);
        back.UnadjustedPerformanceThousandths.ShouldBe(10_000);
        back.PerformanceRawThousandths.ShouldBe(8000);
        back.UnadjustedFormAggregate.ShouldBe(55_000);
        back.FormRaw.ShouldBe(44000);
    }

    [Fact]
    public void ColorCupReport_V1_RemainsReadable()
    {
        // v1 payload has no v2 strength fields.
        string v1Json = """{"version":1,"bonusWeightPermille":350,"performanceWeightPermille":300,"formWeightPermille":250,"prestigeWeightPermille":100,"teams":[{"sportingColor":0,"candidateCount":1,"ranking":[{"athleteId":3,"rank":1,"finalRatingThousandths":900,"bonusNormThousandths":1000,"performanceNormThousandths":1000,"formNormThousandths":1000,"prestigeNormThousandths":1000,"bonusRawThousandths":100,"performanceRawThousandths":200,"formRaw":300,"prestigeRaw":400}]}]}""";
        string stored = MtgSoloSports.Features.History.RoundPayloadCodec.Encode(v1Json);
        ColorCupSelectionReportDocument restored = ColorCupSelectionReportDocument.FromStored(stored);
        restored.Teams[0].Ranking[0].AthleteId.ShouldBe(3);
        restored.Teams[0].Ranking[0].PerformanceRawThousandths.ShouldBe(200);
    }

    [Fact]
    public void TypeCupReport_V2_RoundTrips_WithAdjustedValues()
    {
        var candidate = new TypeCupSelectionReportDocument.Candidate(
            11, 1, 1, "Wizard", false, [], 950, 1000, 900, 800, 1000, 6000, 6000, 33000, 200,
            "White League F2", (int)LeagueLevel.Feeder2, 600, 10_000, 55_000);
        var report = new TypeCupSelectionReportDocument(
            TypeCupSelectionReportDocument.PayloadVersion, 1, 350, 300, 250, 100,
            [new TypeCupSelectionReportDocument.Team("Wizard", 1, [candidate])], []);
        TypeCupSelectionReportDocument restored = TypeCupSelectionReportDocument.FromStored(report.ToStored());
        restored.Version.ShouldBe(2);
        TypeCupSelectionReportDocument.Candidate back = restored.Teams[0].Ranking[0];
        back.SourceLeagueName.ShouldBe("White League F2");
        back.SourceLeagueLevel.ShouldBe((int)LeagueLevel.Feeder2);
        back.StrengthFactorPermille.ShouldBe(600);
        back.UnadjustedPerformanceThousandths.ShouldBe(10_000);
        back.PerformanceRawThousandths.ShouldBe(6000);
    }

    [Fact]
    public void AdjustedRaws_FlowThrough_Normalization()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        // Per-color normalization operates on adjusted raws.
        List<ColorCupSelection.CandidateRaw> candidates =
        [
            new(1, "Alpha", 0, 0, 10_000, 55_000, 0),
            new(2, "Beta", 0, 0, 8_000, 44_000, 0),
            new(3, "Gamma", 0, 0, 6_000, 33_000, 0),
            new(4, "Delta", 0, 0, 4_000, 22_000, 0),
        ];
        IReadOnlyList<ColorCupSelection.ScoredCandidate> ranking = ColorCupSelection.RankAll(candidates, rules);
        ranking[0].Name.ShouldBe("Alpha");
        ranking[0].PerformanceNormThousandths.ShouldBe(1000);
        ranking[3].PerformanceNormThousandths.ShouldBe(0);
        // Global normalization for Type Cup uses the same adjusted raws.
        List<TypeCupAllocation.CandidateRaw> typeCandidates = candidates
            .Select(c => new TypeCupAllocation.CandidateRaw(c.AthleteId, c.Name, ["Wizard"], null, c.BonusRawThousandths, c.PerformanceRawThousandths, c.FormRaw, c.PrestigeRaw))
            .ToList();
        TypeCupAllocation.AllocationResult allocation = TypeCupAllocation.Allocate(typeCandidates, rules);
        allocation.Teams.Count.ShouldBe(1);
        allocation.UnassignedAthleteIds.Count.ShouldBe(0);
    }
}
