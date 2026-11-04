using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Cups.ChaosCup;
using MtgSoloSports.Features.Cups.RunChaosCupGroup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Careers;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace MtgSoloSports.Tests.Features.Diagnostics;

/// <summary>
/// MSS-118 Chaos calibration. Fast synthetic and forked-database tests run in
/// normal CI; the multi-season simulation is opt-in
/// (<c>MTG_CHAOS_CALIBRATION=1</c>) outside normal CI. The harness never
/// auto-tunes weights, factors or stage sizes and never modifies approved
/// numbers: outer weights stay 35/30/20/5/10 and the Chaos career weight stays
/// 450.
/// </summary>
public sealed class ChaosCalibrationTests
{
    private readonly ITestOutputHelper _output;

    public ChaosCalibrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void WeightsAndFormat_StayFixed()
    {
        RulesV1 rules = RulesV3.CreateDefault();
        rules.CupBonusWeightPermille.ShouldBe(350);
        rules.CupPerformanceWeightPermille.ShouldBe(300);
        rules.CupFormWeightPermille.ShouldBe(200);
        rules.CupChaosWeightPermille.ShouldBe(50);
        rules.CupPrestigeWeightPermille.ShouldBe(100);
        CareerPerformanceScorer.ChaosCupIndividualWeight.ShouldBe(450);
        CareerPerformanceScorer.WeightsVersion.ShouldBe("MSS-104-v1");

        ChaosCupFormat.FieldSize.ShouldBe(1024);
        ChaosCupFormat.GroupSize.ShouldBe(32);
        ChaosCupFormat.RoundsPerGroup.ShouldBe(16);
        ChaosCupFormat.AdvancingPerGroup.ShouldBe(16);
        ChaosCupFormat.FinalFieldSize.ShouldBe(32);
        ChaosCupFormat.EntrantsPerColor.ShouldBe(128);
        ChaosCupFormat.FirstStagePerColorPerGroup.ShouldBe(4);
        ChaosCupFormat.StageGroupCounts.ShouldBe([32, 16, 8, 4, 2, 1]);
        ChaosCupFormat.ChaosFormatVersion.ShouldBe(1);

        ChaosCalibrationHarness.TierOrder.ShouldBe(["Super", "F1", "F2", "F3", "Pool"]);
        ChaosCalibrationHarness.ReadFromEnvironmentOrDefault(5, 1UL, 2UL).Seasons.ShouldBe(5);
    }

    [Fact]
    public void FiveSuperP6Careers_OutscoreOneChaosChampion()
    {
        // Five mid-table Superleague careers comfortably outscore one Chaos title,
        // so the 450 weight rewards a Chaos run without trapping elite careers.
        int champion = Score(CareerPerformanceScorer.Category.ChaosCupIndividual, 1, 32, true);
        champion.ShouldBe(675);
        int superP6 = Score(CareerPerformanceScorer.Category.Superleague, 6, 32, true);
        superP6.ShouldBeGreaterThan(champion);
        checked
        {
            (superP6 * 5).ShouldBeGreaterThan(champion);
        }

        ChaosCalibrationHarness.BuildCareerReference().FiveSuperP6Total.ShouldBe(checked(superP6 * 5));
        ChaosCalibrationHarness.BuildCareerReference().ChaosChampion.ShouldBe(675);
    }

    [Fact]
    public void FinalP32_StrictlyExceedsFifthStageExit()
    {
        // The Final's last place (P32) must stay above the best elimination
        // credit (fifth stage = 2-group semifinal exit, Rank 1 = 10).
        int finalLast = Score(CareerPerformanceScorer.Category.ChaosCupIndividual, 32, 32, true);
        finalLast.ShouldBe(13);
        CareerPerformanceScorer.ChaosExitPointsForRank(1).ShouldBe(10);
        finalLast.ShouldBeGreaterThan(CareerPerformanceScorer.ChaosExitPointsForRank(1));

        CareerPerformanceScorer.ChaosExitPointsForRank(5).ShouldBe(1);
        CareerPerformanceScorer.ChaosExitPointsForRank(2).ShouldBe(7);
        CareerPerformanceScorer.ChaosExitPointsForRank(3).ShouldBe(4);
        CareerPerformanceScorer.ChaosExitPointsForRank(4).ShouldBe(2);
        ChaosSelectionForm.ForFinal(1).ShouldBeGreaterThan(ChaosSelectionForm.ForElimination(4));
    }

    [Fact]
    public void PoolChampion_LaterInF3_KeepsBothCredits()
    {
        // A Pool invite that wins Chaos keeps the 675-point champion credit and
        // a later F3 placement adds its own category points; neither overwrites
        // the other and every finalized finish stays positive.
        const int poolChampion = 11;
        var events = new List<CareerPerformanceScorer.PlacementEvent>
        {
            new(poolChampion, 1, CareerPerformanceScorer.Category.ChaosCupIndividual, 1, 32, true),
            new(poolChampion, 2, CareerPerformanceScorer.Category.Feeder3, 1, 32, true),
        };
        IReadOnlyDictionary<int, CareerPerformanceScorer.AthleteCareerScore> scores =
            CareerPerformanceScorer.ComputeScores(events);
        CareerPerformanceScorer.AthleteCareerScore career = scores[poolChampion];
        career.ChaosCupIndividualPoints.ShouldBe(675);
        career.Feeder3Points.ShouldBeGreaterThan(0);
        career.TotalPoints.ShouldBe(checked(career.ChaosCupIndividualPoints + career.Feeder3Points));
        career.MajorWins.ShouldBe(1);
        career.LeagueTitles.ShouldBe(1);
    }

    [Fact]
    public void ChaosAttribution_CloseCallFlips_LargeGapHolds()
    {
        // Identical bonus/perf/form/prestige: the 5% Chaos swing (max 50 points)
        // flips a close call but cannot cover a large bonus gap.
        var close = new List<(int AthleteId, int Final, int ChaosNorm, int Rank)>
        {
            (1, 800, 1000, 1),
            (2, 795, 0, 2),
            (3, 700, 0, 3),
            (4, 690, 0, 4),
            (5, 790, 1000, 5),
        };
        ChaosCalibrationHarness.IsFlipped(close, 50, out _).ShouldBeTrue();

        var gap = new List<(int AthleteId, int Final, int ChaosNorm, int Rank)>
        {
            (1, 1000, 0, 1),
            (2, 900, 1000, 2),
            (3, 890, 1000, 3),
            (4, 880, 1000, 4),
            (5, 600, 0, 5),
        };
        ChaosCalibrationHarness.IsFlipped(gap, 50, out _).ShouldBeFalse();
    }

    [Fact]
    public void TierForEntrant_MapsActivesAndPool()
    {
        ChaosCalibrationHarness.TierForEntrant(PoolInvite()).ShouldBe("Pool");
        ChaosCalibrationHarness.TierForEntrant(Active((int)LeagueKind.Superleague, 0)).ShouldBe("Super");
        ChaosCalibrationHarness.TierForEntrant(Active(0, 1)).ShouldBe("F1");
        ChaosCalibrationHarness.TierForEntrant(Active(0, 2)).ShouldBe("F2");
        ChaosCalibrationHarness.TierForEntrant(Active(0, 3)).ShouldBe("F3");

        static ChaosCupEntrantEntity PoolInvite() => new()
        {
            SourceSeasonId = 1,
            SourceSeasonNumber = 1,
            SaveAthleteId = 9,
            SportingColor = 0,
            IsPoolInvite = true,
        };

        static ChaosCupEntrantEntity Active(int kind, int division) => new()
        {
            SourceSeasonId = 1,
            SourceSeasonNumber = 1,
            SaveAthleteId = 10 + kind + division,
            SportingColor = 0,
            SourceLeagueKind = kind,
            SourceFeederDivision = division,
        };
    }

    [Fact]
    public async Task ManuallyInterleaved_VsBulkGroup_RngEquivalence()
    {
        // Incidental Season 1 history plus the Chaos field draw are forked; the
        // named action is the stage-0 group simulation below, run bulk on one
        // fork and round-interleaved across two groups on the other.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-cal-equiv-");
        try
        {
            await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            var (storeA, rootA, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-cal-bulk-");
            var (storeB, rootB, _) = await TestSaveStores.ForkAsync(store, saveId, "mtgsolosports-chaos-cal-inter-");
            try
            {
                RunChaosCupGroupHandler bulk = new(storeA);
                await bulk.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
                await bulk.FinishGroupAsync(saveId, stageIndex: 0, groupNumber: 2, sourceSeasonNumber: 1);

                RunChaosCupGroupHandler interleaved = new(storeB);
                for (int round = 0; round < ChaosCupFormat.RoundsPerGroup; round++)
                {
                    await interleaved.PlayNextAsync(saveId, stageIndex: 0, groupNumber: 1, sourceSeasonNumber: 1);
                    await interleaved.PlayNextAsync(saveId, stageIndex: 0, groupNumber: 2, sourceSeasonNumber: 1);
                }

                await AssertGroupsEqualAsync(storeA, storeB, saveId, stageIndex: 0, groupNumber: 1);
                await AssertGroupsEqualAsync(storeA, storeB, saveId, stageIndex: 0, groupNumber: 2);
            }
            finally
            {
                TestSaveStores.DeleteRoot(rootA);
                TestSaveStores.DeleteRoot(rootB);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task OptIn_ChaosCalibration_SimulatesAndAggregates()
    {
        string? flag = Environment.GetEnvironmentVariable("MTG_CHAOS_CALIBRATION");
        if (!string.Equals(flag, "1", StringComparison.Ordinal))
        {
            ChaosCalibrationHarness.ReadFromEnvironmentOrDefault(5, 1UL, 2UL).Seasons.ShouldBe(5);
            return;
        }

        ChaosCalibrationHarness.Options options =
            ChaosCalibrationHarness.ReadFromEnvironmentOrDefault(5, 4242UL, 777UL);
        options.Seasons.ShouldBeGreaterThanOrEqualTo(1);
        options.Seasons.ShouldBeLessThanOrEqualTo(500);

        var (store, root) = CreateStore();
        try
        {
            ChaosCalibrationHarness.Report report = await ChaosCalibrationHarness.RunAsync(
                store, UniverseTestCatalog.Build(), options);
            report.SeasonsCompleted.ShouldBe(options.Seasons);
            report.Editions.Count.ShouldBe(options.Seasons);
            report.BonusWeightPermille.ShouldBe(350);
            report.PerformanceWeightPermille.ShouldBe(300);
            report.FormWeightPermille.ShouldBe(200);
            report.ChaosWeightPermille.ShouldBe(50);
            report.PrestigeWeightPermille.ShouldBe(100);
            report.ChaosCupWeight.ShouldBe(450);
            foreach (ChaosCalibrationHarness.EditionSummary edition in report.Editions)
            {
                edition.EntrantCount.ShouldBe(1024);
                edition.StageGroupCounts.ShouldBe([32, 16, 8, 4, 2, 1]);
                edition.FinalistCount.ShouldBe(32);
                edition.MedalCount.ShouldBe(3);
                edition.ByTier.Sum(t => t.Entrants).ShouldBe(1024);
                edition.ByTier.Sum(t => t.Finalists).ShouldBe(32);
                edition.ByTier.Sum(t => t.Medals).ShouldBe(3);
                edition.ByColor.Sum(c => c.Entrants).ShouldBe(1024);
                edition.ByColor.Sum(c => c.Finalists).ShouldBe(32);
                foreach (ChaosCalibrationHarness.ColorCounts color in edition.ByColor)
                {
                    color.Entrants.ShouldBe(128);
                }
            }

            // Same persisted state re-aggregates to the identical checksum.
            ChaosCalibrationHarness.Report again = await ChaosCalibrationHarness.AggregateAsync(
                store, report.SaveId, options, report.SeasonsCompleted);
            string.Equals(again.Checksum, report.Checksum, StringComparison.Ordinal).ShouldBeTrue();
            _output.WriteLine(ChaosCalibrationHarness.RenderText(report));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task AssertGroupsEqualAsync(SaveStore storeA, SaveStore storeB, Guid saveId, int stageIndex, int groupNumber)
    {
        List<ChaosCupRoundPayloadDocument> roundsA = await LoadGroupPayloadsAsync(storeA, saveId, stageIndex, groupNumber).ConfigureAwait(false);
        List<ChaosCupRoundPayloadDocument> roundsB = await LoadGroupPayloadsAsync(storeB, saveId, stageIndex, groupNumber).ConfigureAwait(false);
        roundsA.Count.ShouldBe(ChaosCupFormat.RoundsPerGroup);
        roundsB.Count.ShouldBe(ChaosCupFormat.RoundsPerGroup);
        for (int i = 0; i < roundsA.Count; i++)
        {
            roundsA[i].Checksum.ShouldBe(roundsB[i].Checksum);
            roundsA[i].Placements.Count.ShouldBe(roundsB[i].Placements.Count);
            for (int j = 0; j < roundsA[i].Placements.Count; j++)
            {
                roundsA[i].Placements[j].ShouldBe(roundsB[i].Placements[j]);
            }
        }

        List<ChaosCupGroupStandingEntity> standingsA = await LoadGroupStandingsAsync(storeA, saveId, stageIndex, groupNumber).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> standingsB = await LoadGroupStandingsAsync(storeB, saveId, stageIndex, groupNumber).ConfigureAwait(false);
        standingsA.Count.ShouldBe(ChaosCupFormat.GroupSize);
        standingsB.Count.ShouldBe(ChaosCupFormat.GroupSize);
        foreach (var (a, b) in standingsA.OrderBy(s => s.GroupRank).Zip(standingsB.OrderBy(s => s.GroupRank)))
        {
            a.SaveAthleteId.ShouldBe(b.SaveAthleteId);
            a.GroupRank.ShouldBe(b.GroupRank);
            a.GroupScoreThousandths.ShouldBe(b.GroupScoreThousandths);
        }
    }

    private static async Task<List<ChaosCupRoundPayloadDocument>> LoadGroupPayloadsAsync(SaveStore store, Guid saveId, int stageIndex, int groupNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<ChaosCupRoundEntity> rows = await context.ChaosCupRounds
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.GroupNumber == groupNumber)
            .OrderBy(e => e.RoundNumber)
            .ToListAsync().ConfigureAwait(false);
        return rows.Select(r => ChaosCupRoundPayloadDocument.FromStored(r.PayloadJson)).ToList();
    }

    private static async Task<List<ChaosCupGroupStandingEntity>> LoadGroupStandingsAsync(SaveStore store, Guid saveId, int stageIndex, int groupNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        return await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == stageIndex && e.GroupNumber == groupNumber)
            .ToListAsync().ConfigureAwait(false);
    }

    private static int Score(CareerPerformanceScorer.Category category, int rank, int fieldSize, bool isFinal) =>
        CareerPerformanceScorer.ScoreEvent(new CareerPerformanceScorer.PlacementEvent(7, 3, category, rank, fieldSize, isFinal));

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-chaoscal-" + Guid.NewGuid().ToString("N"));
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
