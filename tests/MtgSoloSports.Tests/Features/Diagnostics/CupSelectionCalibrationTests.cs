using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// MSS-066 calibration tests. Fast synthetic aggregation runs in normal CI;
/// the multi-season simulation is opt-in (<c>MTG_CUP_CALIBRATION=1</c>) outside
/// normal CI. The harness never auto-tunes weights or factors and never
/// enforces tier quotas: it only reports what the persisted selection reports
/// already decided.
/// </summary>
public sealed class CupSelectionCalibrationTests
{
    private readonly ITestOutputHelper _output;

    public CupSelectionCalibrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TierKey_MapsLevels_PoolDefaultsToPool()
    {
        CupSelectionCalibrationHarness.TierKey(0).ShouldBe("Super");
        CupSelectionCalibrationHarness.TierKey(1).ShouldBe("F1");
        CupSelectionCalibrationHarness.TierKey(2).ShouldBe("F2");
        CupSelectionCalibrationHarness.TierKey(3).ShouldBe("F3");
        CupSelectionCalibrationHarness.TierKey(null).ShouldBe("Pool");
        CupSelectionCalibrationHarness.TierKey(99).ShouldBe("Pool");
        CupSelectionCalibrationHarness.TierRank("Super").ShouldBe(0);
        CupSelectionCalibrationHarness.TierRank("F3").ShouldBe(3);
        CupSelectionCalibrationHarness.TierRank("Pool").ShouldBe(4);
    }

    [Fact]
    public void SyntheticColorShortlist_DetectsCrossTierUpset_AndNearestMiss()
    {
        RulesV3 rules = RulesV3.CreateDefault();
        ColorCupSelectionReportDocument document = SyntheticColorDocument();
        Dictionary<int, int?> tiers = new()
        {
            [1] = 0,
            [2] = 1,
            [3] = 2,
            [4] = 3,
            [5] = 0,
        };
        Dictionary<int, string> names = new()
        {
            [1] = "Super Weak",
            [2] = "F1 Mid",
            [3] = "F2 Strong",
            [4] = "F3 Strong",
            [5] = "Super Weaker",
        };
        List<CupSelectionCalibrationHarness.NearestMissComparison> misses = [];
        List<CupSelectionCalibrationHarness.CrossTierExample> examples = [];
        CupSelectionCalibrationHarness.EditionSummary edition =
            CupSelectionCalibrationHarness.SummarizeColorEdition(
                1, rules.Version, document, tiers, names, misses, examples, rules);

        edition.ByTier.Single(t => string.Equals(t.Tier, "Super", StringComparison.Ordinal)).Selected.ShouldBe(1);
        edition.ByTier.Single(t => string.Equals(t.Tier, "F1", StringComparison.Ordinal)).Selected.ShouldBe(1);
        edition.ByTier.Single(t => string.Equals(t.Tier, "F2", StringComparison.Ordinal)).Selected.ShouldBe(1);
        edition.ByTier.Single(t => string.Equals(t.Tier, "F3", StringComparison.Ordinal)).Selected.ShouldBe(1);
        misses.Count.ShouldBe(1);
        misses[0].SelectedTier.ShouldBe("Super");
        misses[0].MissedTier.ShouldBe("Super");
        examples.Any(e =>
            string.Equals(e.SelectedTier, "F3", StringComparison.Ordinal)
            && string.Equals(e.MissedTier, "Super", StringComparison.Ordinal)).ShouldBeTrue();
        // No quota is enforced: the report is honest about a lower-tier athlete
        // beating a higher-tier one on rating rather than hiding it.
        examples.ShouldNotBeEmpty();
    }

    [Fact]
    public void SyntheticTypeShortlist_DetectsCrossTierUpset()
    {
        RulesV3 rules = RulesV3.CreateDefault();
        TypeCupSelectionReportDocument document = SyntheticTypeDocument();
        Dictionary<int, int?> tiers = new()
        {
            [11] = 0,
            [12] = 3,
            [13] = 1,
            [14] = 1,
            [15] = 2,
        };
        Dictionary<int, string> names = new()
        {
            [11] = "Super Weak",
            [12] = "F3 Strong",
            [13] = "F1 Mid A",
            [14] = "F1 Mid B",
            [15] = "F2 Mid",
        };
        List<CupSelectionCalibrationHarness.NearestMissComparison> misses = [];
        List<CupSelectionCalibrationHarness.CrossTierExample> examples = [];
        CupSelectionCalibrationHarness.EditionSummary edition =
            CupSelectionCalibrationHarness.SummarizeTypeEdition(
                2, rules.Version, document, tiers, names, misses, examples, rules);

        edition.ByTier.Single(t => string.Equals(t.Tier, "F3", StringComparison.Ordinal)).Selected.ShouldBe(1);
        examples.Any(e =>
            string.Equals(e.SelectedTier, "F3", StringComparison.Ordinal)
            && string.Equals(e.MissedTier, "Super", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public void Checksum_IsDeterministic_ForSameEditions()
    {
        RulesV3 rules = RulesV3.CreateDefault();
        ColorCupSelectionReportDocument document = SyntheticColorDocument();
        Dictionary<int, int?> tiers = new()
        {
            [1] = 0,
            [2] = 1,
            [3] = 2,
            [4] = 3,
            [5] = 0,
        };
        Dictionary<int, string> names = new()
        {
            [1] = "Super Weak",
            [2] = "F1 Mid",
            [3] = "F2 Strong",
            [4] = "F3 Strong",
            [5] = "Super Weaker",
        };
        List<CupSelectionCalibrationHarness.EditionSummary> first = [Summarize(tiers, names, rules, document)];
        List<CupSelectionCalibrationHarness.EditionSummary> second = [Summarize(tiers, names, rules, document)];
        string one = CupSelectionCalibrationHarness.ComputeChecksum(first);
        string two = CupSelectionCalibrationHarness.ComputeChecksum(second);
        string.Equals(one, two, StringComparison.Ordinal).ShouldBeTrue();

        static CupSelectionCalibrationHarness.EditionSummary Summarize(
            Dictionary<int, int?> tierMap,
            Dictionary<int, string> nameMap,
            RulesV3 snapshot,
            ColorCupSelectionReportDocument report)
        {
            List<CupSelectionCalibrationHarness.NearestMissComparison> localMisses = [];
            List<CupSelectionCalibrationHarness.CrossTierExample> localExamples = [];
            return CupSelectionCalibrationHarness.SummarizeColorEdition(
                1, snapshot.Version, report, tierMap, nameMap, localMisses, localExamples, snapshot);
        }
    }

    [Fact]
    public void CalibrationWeights_StayFixed_AndHarnessBoundsHold()
    {
        RulesV3 rules = RulesV3.CreateDefault();
        rules.CupBonusWeightPermille.ShouldBe(350);
        rules.CupPerformanceWeightPermille.ShouldBe(300);
        rules.CupFormWeightPermille.ShouldBe(250);
        rules.CupPrestigeWeightPermille.ShouldBe(100);
        rules.GetCupStrengthFactor(MtgSoloSports.SimulationKernel.Leagues.LeagueLevel.Superleague).ShouldBe(1000);
        rules.GetCupStrengthFactor(MtgSoloSports.SimulationKernel.Leagues.LeagueLevel.Feeder1).ShouldBe(800);
        rules.GetCupStrengthFactor(MtgSoloSports.SimulationKernel.Leagues.LeagueLevel.Feeder2).ShouldBe(600);
        rules.GetCupStrengthFactor(MtgSoloSports.SimulationKernel.Leagues.LeagueLevel.Feeder3).ShouldBe(400);
        CupSelectionCalibrationHarness.ReadFromEnvironmentOrDefault(6, 1UL, 2UL).Seasons.ShouldBe(6);
        CupSelectionCalibrationHarness.TierOrder.Count.ShouldBe(5);
    }

    [Fact]
    public async Task OptIn_CupCalibration_SimulatesAndAggregates()
    {
        string? flag = Environment.GetEnvironmentVariable("MTG_CUP_CALIBRATION");
        if (!string.Equals(flag, "1", StringComparison.Ordinal))
        {
            CupSelectionCalibrationHarness.ReadFromEnvironmentOrDefault(6, 1UL, 2UL).Seasons.ShouldBe(6);
            return;
        }

        CupSelectionCalibrationHarness.Options options =
            CupSelectionCalibrationHarness.ReadFromEnvironmentOrDefault(6, 4242UL, 777UL);
        options.Seasons.ShouldBeGreaterThanOrEqualTo(1);
        options.Seasons.ShouldBeLessThanOrEqualTo(500);

        var (store, root) = CreateStore();
        try
        {
            CupSelectionCalibrationHarness.Report report = await CupSelectionCalibrationHarness.RunAsync(
                store, UniverseTestCatalog.Build(), options);
            report.SeasonsCompleted.ShouldBe(options.Seasons);
            report.Editions.Count.ShouldBe(options.Seasons);
            report.BonusWeightPermille.ShouldBe(350);
            report.PerformanceWeightPermille.ShouldBe(300);
            report.FormWeightPermille.ShouldBe(250);
            report.PrestigeWeightPermille.ShouldBe(100);
            report.SuperFactorPermille.ShouldBe(1000);
            report.Feeder1FactorPermille.ShouldBe(800);
            report.Feeder2FactorPermille.ShouldBe(600);
            report.Feeder3FactorPermille.ShouldBe(400);
            if (options.Seasons >= 2)
            {
                report.Editions.Any(e => string.Equals(e.Cup, "ColorCup", StringComparison.Ordinal)).ShouldBeTrue();
                report.Editions.Any(e => string.Equals(e.Cup, "TypeCup", StringComparison.Ordinal)).ShouldBeTrue();
            }

            // Same persisted state re-aggregates to the identical checksum.
            CupSelectionCalibrationHarness.Report again = await CupSelectionCalibrationHarness.AggregateAsync(
                store, report.SaveId, options, report.SeasonsCompleted);
            string.Equals(again.Checksum, report.Checksum, StringComparison.Ordinal).ShouldBeTrue();
            _output.WriteLine(CupSelectionCalibrationHarness.RenderText(report));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ColorCupSelectionReportDocument SyntheticColorDocument()
    {
        ColorCupSelectionReportDocument.Candidate f2 = Candidate(3, 1, 900, 800, 1000, 900, 500, 2, 600);
        ColorCupSelectionReportDocument.Candidate f3 = Candidate(4, 2, 850, 700, 900, 850, 400, 3, 400);
        ColorCupSelectionReportDocument.Candidate f1 = Candidate(2, 3, 800, 600, 800, 700, 300, 1, 800);
        ColorCupSelectionReportDocument.Candidate super = Candidate(1, 4, 750, 500, 600, 600, 200, 0, 1000);
        ColorCupSelectionReportDocument.Candidate superOut = Candidate(5, 5, 700, 400, 500, 500, 100, 0, 1000);
        return new ColorCupSelectionReportDocument(
            ColorCupSelectionReportDocument.PayloadVersion, 350, 300, 250, 100,
            [new ColorCupSelectionReportDocument.Team(0, 5, [f2, f3, f1, super, superOut])]);

        static ColorCupSelectionReportDocument.Candidate Candidate(
            int id, int rank, int final, int bonusNorm, int perfNorm, int formNorm, int prestigeNorm, int level, int factor)
        {
            string league = level switch
            {
                0 => "White Superleague",
                1 => "White League F1",
                2 => "White League F2",
                _ => "White League F3",
            };
            return new ColorCupSelectionReportDocument.Candidate(
                id, rank, final, bonusNorm, perfNorm, formNorm, prestigeNorm,
                1000, 5000, 20000, 100,
                league, level, factor, 8000, 30000,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    private static TypeCupSelectionReportDocument SyntheticTypeDocument()
    {
        TypeCupSelectionReportDocument.Candidate f3 = TypeCandidate(12, 1, 1, 900, 3, 400);
        TypeCupSelectionReportDocument.Candidate f1a = TypeCandidate(13, 2, 2, 850, 1, 800);
        TypeCupSelectionReportDocument.Candidate f1b = TypeCandidate(14, 3, 3, 800, 1, 800);
        TypeCupSelectionReportDocument.Candidate f2 = TypeCandidate(15, 4, 4, 750, 2, 600);
        TypeCupSelectionReportDocument.Candidate superOut = TypeCandidate(11, 5, 0, 700, 0, 1000);
        return new TypeCupSelectionReportDocument(
            TypeCupSelectionReportDocument.PayloadVersion, 5, 350, 300, 250, 100,
            [new TypeCupSelectionReportDocument.Team("Elf", 5, [f3, f1a, f1b, f2, superOut])], []);

        static TypeCupSelectionReportDocument.Candidate TypeCandidate(
            int id, int typeRank, int selectionRank, int final, int level, int factor)
        {
            string league = level switch
            {
                0 => "White Superleague",
                1 => "White League F1",
                2 => "White League F2",
                _ => "White League F3",
            };
            return new TypeCupSelectionReportDocument.Candidate(
                id, typeRank, selectionRank, "Elf", false, [],
                final, 600, 800, 700, 300, 1000, 5000, 20000, 100,
                league, level, factor, 8000, 30000,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-cupcal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
