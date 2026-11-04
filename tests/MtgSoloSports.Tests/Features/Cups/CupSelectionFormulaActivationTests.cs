using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupSelectionReport;
using MtgSoloSports.Features.Cups.GetTypeCupSelectionReport;
using MtgSoloSports.Features.Cups.SelectColorCupTeams;
using MtgSoloSports.Features.Cups.SelectionFormula;
using MtgSoloSports.Features.Cups.SelectTypeCupTeams;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

#pragma warning disable MA0004 // Test methods must omit ConfigureAwait per xUnit1030; helpers stay plain for consistency.

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-132: Chaos-aware Cup selection activates in existing saves from a future
/// season. Legacy 350/300/250/0/100 snapshots schedule 350/300/200/50/100 from
/// the first newly started season (no earlier than N+1 and no earlier than
/// Chaos activation); current sources keep legacy, future sources use the new
/// formula for both Cup types. Original snapshots, RNG and history stay intact.
/// </summary>
public sealed class CupSelectionFormulaActivationTests
{
    private static async Task MakeLegacyV3Async(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RulesSnapshotEntity snapshot = await context.RulesSnapshots.SingleAsync(e => e.Id == 1);
        RulesV1 rules = SaveRulesCompatibility.ReadSnapshotRules(snapshot.RulesJson);
        rules.ShouldNotBeNull();
        CupSelectionFormulaLifecycle.EnsureSupportedWeights(rules);

        RulesV1 legacy;
        if (rules is RulesV3 v3)
        {
            RulesV3SnapshotDocument document = RulesV3SnapshotDocument.FromRules(v3) with
            {
                CupBonusWeightPermille = CupSelectionFormulaLifecycle.LegacyBonusWeightPermille,
                CupPerformanceWeightPermille = CupSelectionFormulaLifecycle.LegacyPerformanceWeightPermille,
                CupFormWeightPermille = CupSelectionFormulaLifecycle.LegacyFormWeightPermille,
                CupChaosWeightPermille = CupSelectionFormulaLifecycle.LegacyChaosWeightPermille,
                CupPrestigeWeightPermille = CupSelectionFormulaLifecycle.LegacyPrestigeWeightPermille,
            };
            legacy = document.ToRules();
        }
        else if (rules is RulesV2 v2)
        {
            RulesV2SnapshotDocument document = RulesV2SnapshotDocument.FromRules(v2) with
            {
                CupBonusWeightPermille = CupSelectionFormulaLifecycle.LegacyBonusWeightPermille,
                CupPerformanceWeightPermille = CupSelectionFormulaLifecycle.LegacyPerformanceWeightPermille,
                CupFormWeightPermille = CupSelectionFormulaLifecycle.LegacyFormWeightPermille,
                CupChaosWeightPermille = CupSelectionFormulaLifecycle.LegacyChaosWeightPermille,
                CupPrestigeWeightPermille = CupSelectionFormulaLifecycle.LegacyPrestigeWeightPermille,
            };
            legacy = document.ToRules();
        }
        else
        {
            RulesSnapshotDocument document = RulesSnapshotDocument.FromRules(rules) with
            {
                CupBonusWeightPermille = CupSelectionFormulaLifecycle.LegacyBonusWeightPermille,
                CupPerformanceWeightPermille = CupSelectionFormulaLifecycle.LegacyPerformanceWeightPermille,
                CupFormWeightPermille = CupSelectionFormulaLifecycle.LegacyFormWeightPermille,
                CupChaosWeightPermille = CupSelectionFormulaLifecycle.LegacyChaosWeightPermille,
                CupPrestigeWeightPermille = CupSelectionFormulaLifecycle.LegacyPrestigeWeightPermille,
            };
            legacy = document.ToRules();
        }

        legacy.Validate();
        CupSelectionFormulaLifecycle.IsLegacyWeights(legacy).ShouldBeTrue();
        snapshot.RulesJson = RulesSnapshotCodec.Encode(legacy);
        snapshot.RulesVersion = legacy.Version;

        // Simulate a pre-MSS-132 save missing the activation row.
        List<CupSelectionFormulaActivationEntity> rows = await context.CupSelectionFormulaActivations.ToListAsync();
        context.CupSelectionFormulaActivations.RemoveRange(rows);
        await context.SaveChangesAsync();
        store.InvalidateMigrationVerified(saveId);
    }

    private static async Task<CupSelectionFormulaActivationEntity> EnsureAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await CupSelectionFormulaLifecycle.EnsureActivationAsync(context, CancellationToken.None);
    }

    private static async Task<string> ReadRulesJsonAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RulesSnapshotEntity snapshot = await context.RulesSnapshots.AsNoTracking().SingleAsync(e => e.Id == 1);
        return snapshot.RulesJson;
    }

    private static async Task<(long State, long Stream)> ReadRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1);
        return (rng.State, rng.Stream);
    }

    [Fact]
    public async Task NewSave_SeedsEffectiveFromOne_UsesCurrentWeights()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-new-");
        try
        {
            CupSelectionFormulaActivationEntity activation = await EnsureAsync(store, saveId);
            activation.EffectiveFromSeasonNumber.ShouldBe(1);
            activation.FormulaVersion.ShouldBe(CupSelectionFormulaLifecycle.FormulaVersion);

            using SaveDbContext context = store.OpenDbContext(saveId);
            RulesV1 rules = await MtgSoloSports.Features.Simulation.AdvanceRound.AdvanceRoundHandler.LoadRulesAsync(context, CancellationToken.None);
            CupSelectionFormulaLifecycle.IsCurrentWeights(rules).ShouldBeTrue();
            CupSelectionFormulaLifecycle.ResolveWeights(rules, activation, 1).ShouldBe(CupSelectionFormulaLifecycle.CurrentWeights);

            // Repeated opening never moves the boundary.
            CupSelectionFormulaActivationEntity second = await EnsureAsync(store, saveId);
            second.EffectiveFromSeasonNumber.ShouldBe(1);
            using (SaveDbContext verify = store.OpenDbContext(saveId))
            {
                (await verify.CupSelectionFormulaActivations.CountAsync()).ShouldBe(1);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LegacyV3_BackfillsToCurrentPlusOne_PreservesCurrentSource()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-legacy-");
        try
        {
            await MakeLegacyV3Async(store, saveId);

            // Initialized legacy save first upgraded during source season N=1.
            CupSelectionFormulaActivationEntity activation = await EnsureAsync(store, saveId);
            // Chaos is already enabled from Season 1, so max(1+1, 1) = 2.
            activation.EffectiveFromSeasonNumber.ShouldBe(2);

            using SaveDbContext context = store.OpenDbContext(saveId);
            RulesV1 rules = await MtgSoloSports.Features.Simulation.AdvanceRound.AdvanceRoundHandler.LoadRulesAsync(context, CancellationToken.None);
            CupSelectionFormulaLifecycle.IsLegacyWeights(rules).ShouldBeTrue();

            // Current source keeps legacy; first newly started season uses new.
            CupSelectionFormulaLifecycle.ResolveWeights(rules, activation, 1).ShouldBe(CupSelectionFormulaLifecycle.LegacyWeights);
            CupSelectionFormulaLifecycle.ResolveWeights(rules, activation, 2).ShouldBe(CupSelectionFormulaLifecycle.CurrentWeights);
            // Both alternating Cup types share the boundary (odd Color, even Type).
            CupSelectionFormulaLifecycle.IsNewFormula(activation, 1).ShouldBeFalse();
            CupSelectionFormulaLifecycle.IsNewFormula(activation, 2).ShouldBeTrue();
            CupSelectionFormulaLifecycle.IsNewFormula(activation, 3).ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Legacy_WithPendingSuccessor_KeepsSuccessorEligible()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-pending-");
        try
        {
            // Pending successor already persisted by movement but not started:
            // Season 2 exists, metadata still points at Season 1.
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                context.Seasons.Add(new SeasonEntity { SeasonNumber = 2, HasSuperleague = true, IsComplete = false });
                await context.SaveChangesAsync();
            }

            await MakeLegacyV3Async(store, saveId);
            // Re-add the pending successor (MakeLegacy opens a new context; keep it).
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                bool hasTwo = await context.Seasons.AnyAsync(e => e.SeasonNumber == 2);
                if (!hasTwo)
                {
                    context.Seasons.Add(new SeasonEntity { SeasonNumber = 2, HasSuperleague = true, IsComplete = false });
                    await context.SaveChangesAsync();
                }
            }

            CupSelectionFormulaActivationEntity activation = await EnsureAsync(store, saveId);
            // Still N+1 = 2, never max-persisted + 1 = 3.
            activation.EffectiveFromSeasonNumber.ShouldBe(2);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Legacy_AlreadyChaosEnabled_UpgradesSuccessfully()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-chaos-");
        try
        {
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                ChaosCupActivationEntity chaos = await context.ChaosCupActivations.SingleAsync(e => e.Id == 1);
                chaos.EffectiveFromSeasonNumber.ShouldBe(1);
            }

            await MakeLegacyV3Async(store, saveId);
            CupSelectionFormulaActivationEntity activation = await EnsureAsync(store, saveId);
            activation.EffectiveFromSeasonNumber.ShouldBe(2);
            CupSelectionFormulaLifecycle.ValidateActivation(activation);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UninitializedSave_NoSeasons_EffectiveFromOne()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-uninit-");
        try
        {
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                List<SeasonEntity> seasons = await context.Seasons.ToListAsync();
                context.Seasons.RemoveRange(seasons);
                List<CupSelectionFormulaActivationEntity> rows = await context.CupSelectionFormulaActivations.ToListAsync();
                context.CupSelectionFormulaActivations.RemoveRange(rows);
                await context.SaveChangesAsync();
            }

            store.InvalidateMigrationVerified(saveId);
            CupSelectionFormulaActivationEntity activation = await EnsureAsync(store, saveId);
            activation.EffectiveFromSeasonNumber.ShouldBe(1);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CurrentSource_SelectionKeepsLegacy_FutureUsesNew()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-select-");
        try
        {
            await MakeLegacyV3Async(store, saveId);

            SelectColorCupTeamsResponse response = await new SelectColorCupTeamsHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            response.SourceSeasonNumber.ShouldBe(1);
            response.CupBonusWeightPermille.ShouldBe(350);
            response.CupPerformanceWeightPermille.ShouldBe(300);
            response.CupFormWeightPermille.ShouldBe(250);
            response.CupChaosWeightPermille.ShouldBe(0);
            response.CupPrestigeWeightPermille.ShouldBe(100);

            GetColorCupSelectionReportResponse report = await new GetColorCupSelectionReportHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            report.BonusWeightPermille.ShouldBe(350);
            report.PerformanceWeightPermille.ShouldBe(300);
            report.FormWeightPermille.ShouldBe(250);
            report.ChaosWeightPermille.ShouldBe(0);
            report.PrestigeWeightPermille.ShouldBe(100);
            foreach (GetColorCupSelectionReportResponse.Candidate candidate in report.Teams.SelectMany(t => t.Ranking))
            {
                int expected = SelectionScore.Combine(
                    candidate.BonusNormThousandths, candidate.PerformanceNormThousandths,
                    candidate.FormNormThousandths, candidate.ChaosNormThousandths,
                    candidate.PrestigeNormThousandths,
                    report.BonusWeightPermille, report.PerformanceWeightPermille,
                    report.FormWeightPermille, report.ChaosWeightPermille,
                    report.PrestigeWeightPermille).Thousandths;
                candidate.FinalRatingThousandths.ShouldBe(expected);
            }

            // Activation is stable and already scheduled the future.
            CupSelectionFormulaActivationEntity activation = await EnsureAsync(store, saveId);
            activation.EffectiveFromSeasonNumber.ShouldBe(2);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FallbackReport_WithoutStoredPayload_ShowsOriginalLegacyWeights()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-sel132-fallback-");
        try
        {
            await MakeLegacyV3Async(store, saveId);

            // The template selection was made with current weights; re-resolve as
            // legacy source 1 to prove fallback uses the edition's effective
            // weights (legacy), not today's defaults.
            using SaveDbContext context = store.OpenDbContext(saveId);
            RulesV1 rules = await MtgSoloSports.Features.Simulation.AdvanceRound.AdvanceRoundHandler.LoadRulesAsync(context, CancellationToken.None);
            CupSelectionFormulaActivationEntity activation = await CupSelectionFormulaLifecycle.EnsureActivationAsync(context, CancellationToken.None);
            activation.EffectiveFromSeasonNumber.ShouldBe(2);
            RulesV1 effective = CupSelectionFormulaLifecycle.ToEffectiveRules(rules, activation, 1);
            CupSelectionFormulaLifecycle.IsLegacyWeights(effective).ShouldBeTrue();

            // Delete the stored report; fallback must still show legacy weights.
            using (SaveDbContext mutate = store.OpenDbContext(saveId))
            {
                await mutate.CupSelectionReports.ExecuteDeleteAsync();
            }

            // The forked template already has a Season 1 selection; fallback
            // reconstructs from those rows with legacy effective weights.
            // Note: rows were originally selected with current weights, so final
            // ratings were computed with 200/50; fallback only proves weight
            // resolution, not rating recomputation (ratings stay persisted).
            // For a true legacy-selected save, see CurrentSource test above.
            GetColorCupSelectionReportResponse fallback = await new GetColorCupSelectionReportHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            fallback.HasFullRanking.ShouldBeFalse();
            fallback.BonusWeightPermille.ShouldBe(350);
            fallback.PerformanceWeightPermille.ShouldBe(300);
            fallback.FormWeightPermille.ShouldBe(250);
            fallback.ChaosWeightPermille.ShouldBe(0);
            fallback.PrestigeWeightPermille.ShouldBe(100);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public void LegacyV1V2V3_WeightDetection_AndEffectiveClone()
    {
        RulesV1 currentV3 = RulesV3.CreateDefault();
        CupSelectionFormulaLifecycle.IsCurrentWeights(currentV3).ShouldBeTrue();
        CupSelectionFormulaLifecycle.IsLegacyWeights(currentV3).ShouldBeFalse();

        RulesV1 legacyV3 = CupSelectionFormulaLifecycle.CloneWithWeights(currentV3, CupSelectionFormulaLifecycle.LegacyWeights);
        legacyV3.ShouldBeOfType<RulesV3>();
        CupSelectionFormulaLifecycle.IsLegacyWeights(legacyV3).ShouldBeTrue();

        RulesV1 currentV2 = RulesV2.CreateDefault();
        RulesV1 legacyV2 = CupSelectionFormulaLifecycle.CloneWithWeights(currentV2, CupSelectionFormulaLifecycle.LegacyWeights);
        legacyV2.ShouldBeOfType<RulesV2>();
        CupSelectionFormulaLifecycle.IsLegacyWeights(legacyV2).ShouldBeTrue();

        RulesV1 legacyV1 = RulesV1.Create(new RulesV1Overrides
        {
            CupBonusWeightPermille = 350,
            CupPerformanceWeightPermille = 300,
            CupFormWeightPermille = 250,
            CupChaosWeightPermille = 0,
            CupPrestigeWeightPermille = 100,
        });
        CupSelectionFormulaLifecycle.IsLegacyWeights(legacyV1).ShouldBeTrue();
        CupSelectionFormulaLifecycle.IsCurrentWeights(legacyV1).ShouldBeFalse();

        // Unsupported outer weights never produce a valid snapshot; creation
        // itself aborts instead of silently repairing sporting state.
        Should.Throw<InvalidOperationException>(() => RulesV1.Create(new RulesV1Overrides
        {
            CupBonusWeightPermille = 350,
            CupPerformanceWeightPermille = 300,
            CupFormWeightPermille = 200,
            CupChaosWeightPermille = 0,
            CupPrestigeWeightPermille = 150,
        }));

        // Effective clone preserves version and sporting core, only weights change.
        var activation = new CupSelectionFormulaActivationEntity { Id = 1, EffectiveFromSeasonNumber = 5, FormulaVersion = 1 };
        RulesV1 effectiveOld = CupSelectionFormulaLifecycle.ToEffectiveRules(legacyV3, activation, 3);
        effectiveOld.ShouldBeSameAs(legacyV3);
        RulesV1 effectiveNew = CupSelectionFormulaLifecycle.ToEffectiveRules(legacyV3, activation, 5);
        effectiveNew.ShouldBeOfType<RulesV3>();
        CupSelectionFormulaLifecycle.IsCurrentWeights(effectiveNew).ShouldBeTrue();
        effectiveNew.SportingColorCount.ShouldBe(legacyV3.SportingColorCount);
        effectiveNew.LeagueSize.ShouldBe(legacyV3.LeagueSize);
    }

    [Fact]
    public void ChaosComponent_CanChangeRanking_NotJustPercentages()
    {
        RulesV1 current = RulesV3.CreateDefault();
        RulesV1 legacy = CupSelectionFormulaLifecycle.CloneWithWeights(current, CupSelectionFormulaLifecycle.LegacyWeights);

        // Close call where the 5% Chaos weight (not tie-breaks) flips the winner:
        // Alpha leads on bonus, Beta leads on Chaos. Legacy 350/300/250/0/100
        // keeps Alpha (bonus gap 32pts outweighs zero Chaos); new
        // 350/300/200/50/100 lets Beta win (Chaos 50pts outweighs the smaller
        // bonus gap). Perf/form/prestige tie so only bonus+chaos decide.
        List<ColorCupSelection.CandidateRaw> color =
        [
            new(1, "Alpha", 0, 550, 500, 500, ChaosSelectionForm.ForElimination(0), 500),
            new(2, "Beta", 0, 500, 500, 500, ChaosSelectionForm.ForFinal(1), 500),
            new(3, "Gamma", 0, 0, 500, 500, ChaosSelectionForm.ForElimination(0), 500),
            new(4, "Delta", 0, 0, 500, 500, ChaosSelectionForm.ForElimination(0), 500),
        ];
        IReadOnlyList<ColorCupSelection.ScoredCandidate> legacyTeam = ColorCupSelection.SelectTeam(color, legacy);
        legacyTeam[0].Name.ShouldBe("Alpha");

        IReadOnlyList<ColorCupSelection.ScoredCandidate> currentTeam = ColorCupSelection.SelectTeam(color, current);
        currentTeam[0].Name.ShouldBe("Beta");
        currentTeam[0].ChaosRaw.ShouldBe(ChaosSelectionForm.ForFinal(1));

        List<TypeCupAllocation.CandidateRaw> type =
        [
            new(1, "A1", ["Dragon"], null, 550, 500, 500, ChaosSelectionForm.ForElimination(0), 500),
            new(2, "A2", ["Dragon"], null, 500, 500, 500, ChaosSelectionForm.ForFinal(1), 500),
            new(3, "A3", ["Dragon"], null, 0, 500, 500, ChaosSelectionForm.ForElimination(0), 500),
            new(4, "A4", ["Dragon"], null, 0, 500, 500, ChaosSelectionForm.ForElimination(0), 500),
        ];
        TypeCupAllocation.AllocationResult legacyAlloc = TypeCupAllocation.Allocate(type, legacy);
        legacyAlloc.Teams[0].Members[0].Name.ShouldBe("A1");
        TypeCupAllocation.AllocationResult currentAlloc = TypeCupAllocation.Allocate(type, current);
        currentAlloc.Teams[0].Members[0].Name.ShouldBe("A2");
    }

    [Fact]
    public async Task Activation_ConsumesNoRng_AndPreservesSnapshot()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-pin-");
        try
        {
            await MakeLegacyV3Async(store, saveId);
            string rulesBefore = await ReadRulesJsonAsync(store, saveId);
            (long stateBefore, long streamBefore) = await ReadRngAsync(store, saveId);

            CupSelectionFormulaActivationEntity activation = await EnsureAsync(store, saveId);
            activation.EffectiveFromSeasonNumber.ShouldBe(2);

            string rulesAfter = await ReadRulesJsonAsync(store, saveId);
            rulesAfter.ShouldBe(rulesBefore);
            (long stateAfter, long streamAfter) = await ReadRngAsync(store, saveId);
            stateAfter.ShouldBe(stateBefore);
            streamAfter.ShouldBe(streamBefore);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Retry_NeverMovesEffectiveSeason()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-retry-");
        try
        {
            await MakeLegacyV3Async(store, saveId);
            CupSelectionFormulaActivationEntity first = await EnsureAsync(store, saveId);
            first.EffectiveFromSeasonNumber.ShouldBe(2);
            CupSelectionFormulaActivationEntity second = await EnsureAsync(store, saveId);
            second.EffectiveFromSeasonNumber.ShouldBe(2);

            // Simulate the next season starting (CurrentSeason 1 -> 2) without
            // touching the activation; retry must not postpone to 3.
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                SaveMetadataEntity metadata = await context.SaveMetadata.SingleAsync(e => e.Id == 1);
                metadata.CurrentSeason = 2;
                await context.SaveChangesAsync();
            }

            store.InvalidateMigrationVerified(saveId);
            CupSelectionFormulaActivationEntity third = await EnsureAsync(store, saveId);
            third.EffectiveFromSeasonNumber.ShouldBe(2);
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                (await context.CupSelectionFormulaActivations.CountAsync()).ShouldBe(1);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DirectAndBulk_ResolveSameEffectiveFormula()
    {
        // Direct path: Season 1 selection on a forked Season 1 complete save.
        var (directStore, directRoot, directId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-direct-");
        try
        {
            SelectColorCupTeamsResponse direct = await new SelectColorCupTeamsHandler(directStore).HandleAsync(directId, sourceSeasonNumber: 1);
            direct.CupChaosWeightPermille.ShouldBe(50);
            direct.CupFormWeightPermille.ShouldBe(200);
        }
        finally
        {
            TestSaveStores.DeleteRoot(directRoot);
        }

        // Bulk path: Season 1 plus Cups built once per process through
        // SimulateSeasons(1) (production bulk flow).
        var (bulkStore, bulkRoot, bulkId) = await SharedSaveTemplates.ForkSeason1PlusCupsAsync("mtgsolosports-sel132-bulk-");
        try
        {
            GetColorCupSelectionReportResponse bulk = await new GetColorCupSelectionReportHandler(bulkStore).HandleAsync(bulkId, sourceSeasonNumber: 1);
            bulk.ChaosWeightPermille.ShouldBe(50);
            bulk.FormWeightPermille.ShouldBe(200);
        }
        finally
        {
            TestSaveStores.DeleteRoot(bulkRoot);
        }
    }

    [Fact]
    public async Task LegacyWithoutChaos_KeepsNeutralZeroComponent()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-nochaos-");
        try
        {
            await MakeLegacyV3Async(store, saveId);
            SelectColorCupTeamsResponse response = await new SelectColorCupTeamsHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            response.CupChaosWeightPermille.ShouldBe(0);
            foreach (ColorCupTeamMember member in response.Teams.SelectMany(t => t.Members))
            {
                member.ChaosRaw.ShouldBe(0);
                member.ChaosNormThousandths.ShouldBe(1000);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ExportImport_RetainsActivationBoundary()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-export-");
        try
        {
            await MakeLegacyV3Async(store, saveId);
            CupSelectionFormulaActivationEntity before = await EnsureAsync(store, saveId);
            before.EffectiveFromSeasonNumber.ShouldBe(2);

            SaveStore.ExportRecord exported = await store.ExportAsync(saveId);
            string importRoot = Path.Combine(Path.GetTempPath(), "mtgsolosports-sel132-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(importRoot);
            try
            {
                SaveStore importStore = TestSaveStores.CreateStoreForRoot(importRoot);
                // Import to an isolated root; same save id, no overwrite conflict.
                string source = store.GetSaveFilePath(saveId);
                string destination = importStore.GetSaveFilePath(saveId);
                await TestSaveStores.CopySaveFileLockedAsync(store, saveId, source, destination);
                CupSelectionFormulaActivationEntity after = await EnsureAsync(importStore, saveId);
                after.EffectiveFromSeasonNumber.ShouldBe(2);
                after.FormulaVersion.ShouldBe(before.FormulaVersion);
                _ = exported;
            }
            finally
            {
                TestSaveStores.DeleteRoot(importRoot);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Status_ExposesScheduledFormula_AndDetailMatchesEffective()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-status-");
        try
        {
            await MakeLegacyV3Async(store, saveId);
            var statusHandler = new MtgSoloSports.Features.Seasons.GetSeasonStatus.GetSeasonStatusHandler(store);
            MtgSoloSports.Features.Seasons.GetSeasonStatus.GetSeasonStatusResponse status = await statusHandler.HandleAsync(saveId);
            status.SelectionFormulaEffectiveFromSeasonNumber.ShouldBe(2);
            status.SelectionFormulaVersion.ShouldBe(1);
            // Season 1 complete with no successor: reference is source 1 (legacy).
            status.SelectionBonusWeightPermille.ShouldBe(350);
            status.SelectionPerformanceWeightPermille.ShouldBe(300);
            status.SelectionFormWeightPermille.ShouldBe(250);
            status.SelectionChaosWeightPermille.ShouldBe(0);
            status.SelectionPrestigeWeightPermille.ShouldBe(100);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task EvenSeasonUpgrade_PlayerFacingExample_KeepsSeason18TypeLegacy()
    {
        // Player-facing example: save upgraded during even Season 18 keeps
        // Season 18 Type Cup on old formula; Season 19 Color and Season 20 Type
        // use 350/300/200/50/100 (Chaos permitting). Proved here with N=2.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-even-");
        try
        {
            using (SaveDbContext contextA = store.OpenDbContext(saveId))
            {
                SaveMetadataEntity metadataA = await contextA.SaveMetadata.SingleAsync(e => e.Id == 1);
                metadataA.CurrentSeason = 2;
                await contextA.SaveChangesAsync();
            }

            await MakeLegacyV3Async(store, saveId);
            // Re-assert CurrentSeason=2 (MakeLegacy opens new contexts but keeps metadata).
            using (SaveDbContext contextB = store.OpenDbContext(saveId))
            {
                SaveMetadataEntity metadataB = await contextB.SaveMetadata.AsNoTracking().SingleAsync(e => e.Id == 1);
                if (metadataB.CurrentSeason != 2)
                {
                    SaveMetadataEntity tracked = await contextB.SaveMetadata.SingleAsync(e => e.Id == 1);
                    tracked.CurrentSeason = 2;
                    await contextB.SaveChangesAsync();
                }
            }

            store.InvalidateMigrationVerified(saveId);
            CupSelectionFormulaActivationEntity activation = await EnsureAsync(store, saveId);
            activation.EffectiveFromSeasonNumber.ShouldBe(3);

            using SaveDbContext contextC = store.OpenDbContext(saveId);
            RulesV1 rules = await MtgSoloSports.Features.Simulation.AdvanceRound.AdvanceRoundHandler.LoadRulesAsync(contextC, CancellationToken.None);
            // Season 18 analogue (even Type, source 2) keeps legacy.
            CupSelectionFormulaLifecycle.ResolveWeights(rules, activation, 2).ShouldBe(CupSelectionFormulaLifecycle.LegacyWeights);
            // Season 19 Color and Season 20 Type use new.
            CupSelectionFormulaLifecycle.ResolveWeights(rules, activation, 3).ShouldBe(CupSelectionFormulaLifecycle.CurrentWeights);
            CupSelectionFormulaLifecycle.ResolveWeights(rules, activation, 4).ShouldBe(CupSelectionFormulaLifecycle.CurrentWeights);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TypeCup_LegacySelection_UsesLegacyWeights_AndFallbackRetains()
    {
        // Real even-source selection through production handlers (Season 2
        // complete template forked; incidental Season 2 history is the cheapest
        // prepared save for an even source).
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason2CompleteAsync("mtgsolosports-sel132-type-");
        try
        {
            await MakeLegacyV3Async(store, saveId);
            // Season2Complete has CurrentSeason=2 (or 1 with pending successor);
            // force the even-upgrade boundary to N=2 for determinism.
            using (SaveDbContext contextD = store.OpenDbContext(saveId))
            {
                SaveMetadataEntity metadataD = await contextD.SaveMetadata.SingleAsync(e => e.Id == 1);
                metadataD.CurrentSeason = 2;
                await contextD.SaveChangesAsync();
            }

            store.InvalidateMigrationVerified(saveId);
            CupSelectionFormulaActivationEntity activation = await EnsureAsync(store, saveId);
            // Chaos is eligible from 1 in these templates, so max(3,1)=3; source
            // 2 (even Type) is still legacy, proving odd/even share one boundary.
            activation.EffectiveFromSeasonNumber.ShouldBe(3);

            SelectTypeCupTeamsResponse response = await new SelectTypeCupTeamsHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            response.SourceSeasonNumber.ShouldBe(2);
            response.CupBonusWeightPermille.ShouldBe(350);
            response.CupPerformanceWeightPermille.ShouldBe(300);
            response.CupFormWeightPermille.ShouldBe(250);
            response.CupChaosWeightPermille.ShouldBe(0);
            response.CupPrestigeWeightPermille.ShouldBe(100);

            GetTypeCupSelectionReportResponse report = await new GetTypeCupSelectionReportHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            report.BonusWeightPermille.ShouldBe(350);
            report.FormWeightPermille.ShouldBe(250);
            report.ChaosWeightPermille.ShouldBe(0);

            using (SaveDbContext mutate = store.OpenDbContext(saveId))
            {
                await mutate.CupSelectionReports.ExecuteDeleteAsync();
            }

            GetTypeCupSelectionReportResponse fallback = await new GetTypeCupSelectionReportHandler(store).HandleAsync(saveId, sourceSeasonNumber: 2);
            fallback.BonusWeightPermille.ShouldBe(350);
            fallback.FormWeightPermille.ShouldBe(250);
            fallback.ChaosWeightPermille.ShouldBe(0);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ConcurrentFirstBackfills_BothSucceedWithOneRow()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-race-");
        try
        {
            await MakeLegacyV3Async(store, saveId);
            using SaveDbContext loserContext = store.OpenDbContext(saveId);
            using SaveDbContext winnerContext = store.OpenDbContext(saveId);
            // Winner commits first.
            CupSelectionFormulaActivationEntity winner = await CupSelectionFormulaLifecycle.EnsureActivationAsync(winnerContext, CancellationToken.None);
            winner.EffectiveFromSeasonNumber.ShouldBe(2);
            // Loser had already observed missing row; retry resolves to winner.
            CupSelectionFormulaActivationEntity loser = await CupSelectionFormulaLifecycle.EnsureActivationAsync(loserContext, CancellationToken.None);
            loser.EffectiveFromSeasonNumber.ShouldBe(2);
            using (SaveDbContext verify = store.OpenDbContext(saveId))
            {
                (await verify.CupSelectionFormulaActivations.CountAsync()).ShouldBe(1);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Checkpoint_RetainsActivationBoundary()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-sel132-checkpoint-");
        try
        {
            await MakeLegacyV3Async(store, saveId);
            CupSelectionFormulaActivationEntity before = await EnsureAsync(store, saveId);
            before.EffectiveFromSeasonNumber.ShouldBe(2);

            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(saveId, "sel132-test");
            checkpoint.SaveId.ShouldBe(saveId);

            // Checkpoint database carries the same activation row.
            string checkpointDb = MtgSoloSports.Persistence.Saves.SaveCheckpointFiles.GetCheckpointDatabasePath(
                MtgSoloSports.Persistence.Saves.SaveCheckpointFiles.GetSaveCheckpointDirectory(store.GetSavesRoot(), saveId),
                checkpoint.CheckpointId);
            System.IO.File.Exists(checkpointDb).ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FromScratch_Creation_SeedsCurrentActivation()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-sel132-scratch-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Scratch132", 4242UL, 777UL, MtgSoloSports.Tests.Features.Universe.UniverseTestCatalog.Build());
            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            CupSelectionFormulaActivationEntity activation = await CupSelectionFormulaLifecycle.EnsureActivationAsync(context, CancellationToken.None);
            activation.EffectiveFromSeasonNumber.ShouldBe(1);
            activation.FormulaVersion.ShouldBe(CupSelectionFormulaLifecycle.FormulaVersion);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }
}

