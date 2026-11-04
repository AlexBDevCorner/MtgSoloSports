using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-105: 35/30/20/5/10 selection with a separate Chaos form component.
/// Chaos form is one outcome per athlete (reached stage and Final rank), never
/// a sum of group scores; legacy seasons with no Chaos Cup score zero cleanly.
/// </summary>
public sealed class ChaosFormSelectionTests
{
    private static RulesV1 Rules() => RulesV3.CreateDefault();

    [Fact]
    public void ChaosForm_MapsExitStagesAndFinalRanks()
    {
        ChaosSelectionForm.ForMissing().ShouldBe(0);
        ChaosSelectionForm.ForElimination(0).ShouldBe(10);
        ChaosSelectionForm.ForElimination(4).ShouldBe(50);
        ChaosSelectionForm.ForFinal(32).ShouldBe(60);
        ChaosSelectionForm.ForFinal(1).ShouldBe(91);
        (ChaosSelectionForm.ForFinal(1) > ChaosSelectionForm.ForElimination(4)).ShouldBeTrue();
        (ChaosSelectionForm.ForElimination(1) > ChaosSelectionForm.ForElimination(0)).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => ChaosSelectionForm.ForElimination(5));
        Should.Throw<InvalidOperationException>(() => ChaosSelectionForm.ForFinal(0));
        Should.Throw<InvalidOperationException>(() => ChaosSelectionForm.ForFinal(33));
    }

    [Fact]
    public void Rules_DefaultWeights_Are35_30_20_5_10()
    {
        RulesV1 rules = Rules();
        rules.CupBonusWeightPermille.ShouldBe(350);
        rules.CupPerformanceWeightPermille.ShouldBe(300);
        rules.CupFormWeightPermille.ShouldBe(200);
        rules.CupChaosWeightPermille.ShouldBe(50);
        rules.CupPrestigeWeightPermille.ShouldBe(100);
        checked
        {
            int total = rules.CupBonusWeightPermille + rules.CupPerformanceWeightPermille
                + rules.CupFormWeightPermille + rules.CupChaosWeightPermille + rules.CupPrestigeWeightPermille;
            total.ShouldBe(1000);
        }
    }

    [Fact]
    public void FiveComponentCombine_SmallChaosImpact_FlipsCloseCallOnly()
    {
        RulesV1 rules = Rules();
        // Identical bonus/perf/form/prestige; chaos decides a close call.
        int closeLow = SelectionScore.Combine(500, 500, 500, 0, 500,
            rules.CupBonusWeightPermille, rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille, rules.CupChaosWeightPermille, rules.CupPrestigeWeightPermille).Thousandths;
        int closeHigh = SelectionScore.Combine(500, 500, 500, 1000, 500,
            rules.CupBonusWeightPermille, rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille, rules.CupChaosWeightPermille, rules.CupPrestigeWeightPermille).Thousandths;
        (closeHigh - closeLow).ShouldBe(50);

        // A large bonus gap (1000 vs 0) is 350 points, far above the 50-point chaos swing.
        int strong = SelectionScore.Combine(1000, 500, 500, 0, 500,
            rules.CupBonusWeightPermille, rules.CupPerformanceWeightPermille,
            rules.CupFormWeightPermille, rules.CupChaosWeightPermille, rules.CupPrestigeWeightPermille).Thousandths;
        (strong - closeHigh).ShouldBeGreaterThan(50);
    }

    [Fact]
    public void ColorSelection_ChaosFinalist_BeatsIdenticalExit()
    {
        RulesV1 rules = Rules();
        List<ColorCupSelection.CandidateRaw> candidates =
        [
            new(1, "Exit", 0, 500, 500, 500, ChaosSelectionForm.ForElimination(0), 500),
            new(2, "Mid", 0, 500, 500, 500, ChaosSelectionForm.ForElimination(2), 500),
            new(3, "Semi", 0, 500, 500, 500, ChaosSelectionForm.ForElimination(4), 500),
            new(4, "Finalist", 0, 500, 500, 500, ChaosSelectionForm.ForFinal(1), 500),
            new(5, "Last", 0, 500, 500, 500, ChaosSelectionForm.ForElimination(0), 499),
        ];

        IReadOnlyList<ColorCupSelection.ScoredCandidate> team = ColorCupSelection.SelectTeam(candidates, rules);
        team[0].Name.ShouldBe("Finalist");
        team[0].ChaosRaw.ShouldBe(ChaosSelectionForm.ForFinal(1));
        team[0].ChaosNormThousandths.ShouldBe(1000);
        // F3-style: chaos ordering is tier-blind; a finalist outranks exits on chaos alone.
        team.Select(t => t.Name).ShouldContain("Finalist", StringComparer.Ordinal);
    }

    [Fact]
    public void TypeAllocation_ChaosFinalist_BeatsIdenticalExit()
    {
        RulesV1 rules = Rules();
        List<TypeCupAllocation.CandidateRaw> candidates =
        [
            new(1, "A1", ["Dragon"], null, 500, 500, 500, ChaosSelectionForm.ForElimination(0), 500),
            new(2, "A2", ["Dragon"], null, 500, 500, 500, ChaosSelectionForm.ForElimination(0), 500),
            new(3, "A3", ["Dragon"], null, 500, 500, 500, ChaosSelectionForm.ForElimination(0), 500),
            new(4, "A4", ["Dragon"], null, 500, 500, 500, ChaosSelectionForm.ForFinal(1), 500),
        ];

        TypeCupAllocation.AllocationResult result = TypeCupAllocation.Allocate(candidates, rules);
        result.Teams.Count.ShouldBe(1);
        result.Teams[0].Members[0].Name.ShouldBe("A4");
        result.Teams[0].Members[0].ChaosRaw.ShouldBe(ChaosSelectionForm.ForFinal(1));
    }

    [Fact]
    public async Task SelectColor_LegacySeasonWithoutChaos_SucceedsWithNeutralChaos()
    {
        // MSS-067: shared Season 1 template forked; no Chaos Cup exists for Season 1.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos105-");
        try
        {
            SelectColorCupTeamsHandler handler = new(store);
            SelectColorCupTeamsResponse response = await handler.HandleAsync(saveId);

            response.CupBonusWeightPermille.ShouldBe(350);
            response.CupPerformanceWeightPermille.ShouldBe(300);
            response.CupFormWeightPermille.ShouldBe(200);
            response.CupChaosWeightPermille.ShouldBe(50);
            response.CupPrestigeWeightPermille.ShouldBe(100);
            checked
            {
                int total = response.CupBonusWeightPermille + response.CupPerformanceWeightPermille
                    + response.CupFormWeightPermille + response.CupChaosWeightPermille + response.CupPrestigeWeightPermille;
                total.ShouldBe(1000);
            }

            foreach (ColorCupTeamResult team in response.Teams)
            {
                foreach (ColorCupTeamMember member in team.Members)
                {
                    member.ChaosRaw.ShouldBe(0);
                    member.ChaosNormThousandths.ShouldBe(1000);
                    int expected = SelectionScore.Combine(
                        member.BonusNormThousandths,
                        member.PerformanceNormThousandths,
                        member.FormNormThousandths,
                        member.ChaosNormThousandths,
                        member.PrestigeNormThousandths,
                        response.CupBonusWeightPermille,
                        response.CupPerformanceWeightPermille,
                        response.CupFormWeightPermille,
                        response.CupChaosWeightPermille,
                        response.CupPrestigeWeightPermille).Thousandths;
                    member.FinalRatingThousandths.ShouldBe(expected);
                }
            }

            using SaveDbContext context = store.OpenDbContext(saveId);
            List<ColorCupSelectionEntity> rows = await context.ColorCupSelections.AsNoTracking().ToListAsync();
            rows.Count.ShouldBe(32);
            rows.ShouldAllBe(r => r.ChaosRaw == 0);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SelectColor_WithCompletedChaos_UsesReachedStageAndFinalRank()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos105-final-");
        try
        {
            await SeedCompletedChaosAsync(store, saveId);
            SelectColorCupTeamsResponse response = await new SelectColorCupTeamsHandler(store).HandleAsync(saveId);
            response.TotalSelected.ShouldBe(32);
            // 5% chaos rewards finalists but never guarantees a single champion; assert the component is wired.
            response.Teams.SelectMany(t => t.Members).Any(m => m.ChaosRaw > 0).ShouldBeTrue();

            using SaveDbContext verify = store.OpenDbContext(saveId);
            SeasonEntity source = await verify.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            Dictionary<int, int> chaos = await SelectColorCupTeamsHandler.LoadChaosRawsAsync(verify, source, CancellationToken.None);
            chaos.Count.ShouldBe(40);
            chaos.Values.ShouldContain(ChaosSelectionForm.ForFinal(1));
            chaos.Values.ShouldContain(ChaosSelectionForm.ForElimination(0));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task SeedCompletedChaosAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<SaveAthleteEntity> actives = await context.SaveAthletes.AsNoTracking().OrderBy(e => e.Id).Take(40).ToListAsync().ConfigureAwait(false);
        actives.Count.ShouldBe(40);

        List<ChaosCupGroupStandingEntity> rows = [];
        for (int i = 0; i < 32; i++)
        {
            rows.Add(ChaosRow(season, actives[i], stage: 5, rank: i + 1, score: 32000 - (i * 100)));
        }

        for (int i = 32; i < 40; i++)
        {
            rows.Add(ChaosRow(season, actives[i], stage: 0, rank: 17 + i, score: 1000));
        }

        context.ChaosCupGroupStandings.AddRange(rows);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static ChaosCupGroupStandingEntity ChaosRow(SeasonEntity season, SaveAthleteEntity athlete, int stage, int rank, int score)
    {
        return new ChaosCupGroupStandingEntity
        {
            SourceSeasonId = season.Id,
            SourceSeasonNumber = season.SeasonNumber,
            StageIndex = stage,
            GroupNumber = 1,
            SaveAthleteId = athlete.Id,
            GroupRank = rank,
            GroupScoreThousandths = score,
            BaseScoreThousandths = score / 2,
            RoundWins = 0,
            RoundPlaceCountsJson = "[]",
            SportingColor = athlete.SportingColor,
            IsAdvanced = false,
        };
    }

    [Fact]
    public void HistoricReport_WithoutChaos_ParsesWithZeroChaos()
    {
        // v4 payload (no chaos fields) must remain readable; missing chaos decodes to zero.
        string json = "{\"version\":4,\"bonusWeightPermille\":350,\"performanceWeightPermille\":300,\"formWeightPermille\":250,\"prestigeWeightPermille\":100,\"teams\":[],\"prestigeWeightsVersion\":\"MSS-104-v1\"}";
        string stored = MtgSoloSports.Features.History.RoundPayloadCodec.Encode(json);
        ColorCupSelectionReportDocument document = ColorCupSelectionReportDocument.FromStored(stored);
        document.ChaosWeightPermille.ShouldBe(0);
        document.BonusWeightPermille.ShouldBe(350);
        document.FormWeightPermille.ShouldBe(250);

        string typeJson = "{\"version\":4,\"candidateCount\":0,\"bonusWeightPermille\":350,\"performanceWeightPermille\":300,\"formWeightPermille\":250,\"prestigeWeightPermille\":100,\"teams\":[],\"missedTypes\":[],\"prestigeWeightsVersion\":\"MSS-104-v1\"}";
        string typeStored = MtgSoloSports.Features.History.RoundPayloadCodec.Encode(typeJson);
        TypeCupSelectionReportDocument typeDocument = TypeCupSelectionReportDocument.FromStored(typeStored);
        typeDocument.ChaosWeightPermille.ShouldBe(0);
    }
}
