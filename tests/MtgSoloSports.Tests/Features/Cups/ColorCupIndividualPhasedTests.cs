using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.PlayColorCupIndividualRound;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Leagues;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using MtgSoloSports.SimulationKernel.Stages;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-120: the confirmed 32-to-16 Color Cup with full score carryover.
/// New Cups play 16 qualifying rounds of 32, cut to 16 finalists, then 16
/// final rounds of 16 with 100% of qualifying points intact. Legacy 16-round
/// editions keep their meaning via the MSS-119 format discriminator.
/// </summary>
public sealed class ColorCupIndividualPhasedTests
{
    [Fact]
    public async Task NewCup_ProducesSixteenPlusSixteen_WithPhasedFormat()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup120-shape-");
        try
        {
            RunColorCupIndividualResponse response = await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            response.Rounds.ShouldBe(32);
            response.Standings.Count.ShouldBe(32);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            List<ColorCupIndividualRoundEntity> rows = await context.ColorCupIndividualRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.RoundNumber).ToListAsync();
            rows.Count.ShouldBe(32);
            rows.Select(r => r.RoundNumber).ShouldBe(Enumerable.Range(1, 32).ToList());
            rows.All(r => r.FormatVersion == ColorCupIndividualFormat.PhasedFormatVersion).ShouldBeTrue();

            List<ColorCupIndividualRoundPayloadDocument> payloads = rows
                .Select(r => ColorCupIndividualRoundPayloadDocument.FromStored(r.PayloadJson)).ToList();
            payloads.All(p => p.FormatVersion == ColorCupIndividualFormat.PhasedFormatVersion).ShouldBeTrue();
            payloads.Take(16).All(p => p.Placements.Count == 32).ShouldBeTrue();
            payloads.Skip(16).All(p => p.Placements.Count == 16).ShouldBeTrue();

            List<ColorCupIndividualStandingEntity> standings = await context.ColorCupIndividualStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.CupRank).ToListAsync();
            standings.Count.ShouldBe(32);
            standings.Select(s => s.CupRank).ShouldBe(Enumerable.Range(1, 32).ToList());
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task NewCup_CutoffQualifiesSixteen_WithFullCarryover()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup120-cutoff-");
        try
        {
            RunColorCupIndividualResponse response = await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            response.Rounds.ShouldBe(32);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            List<ColorCupIndividualRoundEntity> rows = await context.ColorCupIndividualRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.RoundNumber).ToListAsync();
            List<ColorCupIndividualRoundPayloadDocument> payloads = rows
                .Select(r => ColorCupIndividualRoundPayloadDocument.FromStored(r.PayloadJson)).ToList();

            ColorCupIndividualRoundPayloadDocument round16 = payloads[15];
            ColorCupIndividualRoundPayloadDocument round17 = payloads[16];
            Dictionary<int, int> carried = round16.Placements.ToDictionary(p => p.AthleteId, p => p.CumulativeAfterThousandths);
            HashSet<int> finalists = round17.Placements.Select(p => p.AthleteId).ToHashSet();
            finalists.Count.ShouldBe(16);

            foreach (RoundPayloadEntry entry in round17.Placements)
            {
                carried.TryGetValue(entry.AthleteId, out int expected).ShouldBeTrue();
                entry.CumulativeBeforeThousandths.ShouldBe(expected);
            }

            HashSet<int> qualifyingIds = round16.Placements.Select(p => p.AthleteId).ToHashSet();
            finalists.IsSubsetOf(qualifyingIds).ShouldBeTrue();
            foreach (ColorCupIndividualRoundPayloadDocument final in payloads.Skip(16))
            {
                final.Placements.Select(p => p.AthleteId).ToHashSet().SetEquals(finalists).ShouldBeTrue();
            }

            List<ColorCupIndividualStandingEntity> standings = await context.ColorCupIndividualStandings.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.CupRank).ToListAsync();
            HashSet<int> top16 = standings.Take(16).Select(s => s.SaveAthleteId).ToHashSet();
            top16.SetEquals(finalists).ShouldBeTrue();
            HashSet<int> eliminated = standings.Skip(16).Select(s => s.SaveAthleteId).ToHashSet();
            eliminated.Count.ShouldBe(16);
            eliminated.Overlaps(finalists).ShouldBeFalse();

            int minFinalist = standings.Take(16).Min(s => s.CupScoreThousandths);
            int maxEliminated = standings.Skip(16).Max(s => s.CupScoreThousandths);
            (minFinalist > maxEliminated).ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task NewCup_FinalRounds_UseSixteenPrefixScoring()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup120-scoring-");
        try
        {
            await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            List<ColorCupIndividualRoundEntity> rows = await context.ColorCupIndividualRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.RoundNumber).ToListAsync();
            RulesV1 rules = RulesV1.CreateDefault();
            foreach (ColorCupIndividualRoundEntity row in rows.Skip(16))
            {
                ColorCupIndividualRoundPayloadDocument payload = ColorCupIndividualRoundPayloadDocument.FromStored(row.PayloadJson);
                payload.Placements.Count.ShouldBe(16);
                foreach (RoundPayloadEntry placement in payload.Placements)
                {
                    int expectedBase = rules.ScoringTable[placement.Position - 1] * RulesV1.FixedScale;
                    placement.BaseThousandths.ShouldBe(expectedBase);
                }
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task NewCup_RngCheckpoint_Round17MatchesDerivedCutoff()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup120-rng-");
        try
        {
            await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            List<ColorCupIndividualRoundEntity> rows = await context.ColorCupIndividualRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.RoundNumber).ToListAsync();
            List<ColorCupIndividualRoundPayloadDocument> payloads = rows
                .Select(r => ColorCupIndividualRoundPayloadDocument.FromStored(r.PayloadJson)).ToList();
            RulesV1 rules = await AdvanceRoundHandler.LoadRulesAsync(context, CancellationToken.None);

            List<ColorCupIndividualRoundPayloadDocument> qualifying = payloads.Take(16).ToList();
            Pcg32State round16After = new(qualifying[^1].RngAfterState, qualifying[^1].RngAfterStream);
            ColorCupIndividualPhasedOrchestrator.CutoffResolution cutoff =
                ColorCupIndividualPhasedOrchestrator.ResolveCutoff(qualifying, rules, round16After);

            ColorCupIndividualRoundPayloadDocument round17 = payloads[16];
            new Pcg32State(round17.RngBeforeState, round17.RngBeforeStream).ShouldBe(cutoff.RngAfterCutoff);
            round17.Placements.Select(p => p.AthleteId).ToHashSet()
                .SetEquals(new HashSet<int>(cutoff.QualifierAthleteIds)).ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Resume_BeforeCutoff_ConvergesToOneShot()
    {
        var (oneShotStore, oneShotRoot, oneShotId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup120-resume16-");
        var (resumeStore, resumeRoot, resumeId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup120-resume16-");
        try
        {
            await new RunColorCupIndividualHandler(oneShotStore).HandleAsync(oneShotId);
            PlayColorCupIndividualRoundHandler step = new(resumeStore);
            for (int i = 0; i < 16; i++)
            {
                PlayColorCupIndividualRoundResponse played = await step.HandleAsync(resumeId);
                played.RoundsPlayed.ShouldBe(i + 1);
                played.IsComplete.ShouldBeFalse();
            }

            await new RunColorCupIndividualHandler(resumeStore).HandleAsync(resumeId);
            (await SnapshotAsync(resumeStore, resumeId)).ShouldBe(await SnapshotAsync(oneShotStore, oneShotId));
        }
        finally
        {
            TestSaveStores.DeleteRoot(oneShotRoot);
            TestSaveStores.DeleteRoot(resumeRoot);
        }
    }

    [Fact]
    public async Task Resume_AfterCutoff_ConvergesToOneShot()
    {
        var (oneShotStore, oneShotRoot, oneShotId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup120-resume17-");
        var (resumeStore, resumeRoot, resumeId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup120-resume17-");
        try
        {
            await new RunColorCupIndividualHandler(oneShotStore).HandleAsync(oneShotId);
            PlayColorCupIndividualRoundHandler step = new(resumeStore);
            for (int i = 0; i < 17; i++)
            {
                PlayColorCupIndividualRoundResponse played = await step.HandleAsync(resumeId);
                played.RoundsPlayed.ShouldBe(i + 1);
                played.IsComplete.ShouldBeFalse();
            }

            await new RunColorCupIndividualHandler(resumeStore).HandleAsync(resumeId);
            (await SnapshotAsync(resumeStore, resumeId)).ShouldBe(await SnapshotAsync(oneShotStore, oneShotId));
        }
        finally
        {
            TestSaveStores.DeleteRoot(oneShotRoot);
            TestSaveStores.DeleteRoot(resumeRoot);
        }
    }

    [Fact]
    public void Cutoff_Untied_LeavesRngUnchanged()
    {
        List<StageAthleteTotals> totals = BuildDistinctTotals();
        Pcg32State before = new(12345UL, 6789UL);
        ColorCupIndividualPhasedOrchestrator.CutoffResolution first =
            ColorCupIndividualPhasedOrchestrator.ResolveCutoffFromTotals(totals, before);
        first.TieDrawConsumed.ShouldBeFalse();
        first.RngAfterCutoff.ShouldBe(before);
        first.QualifierAthleteIds.Count.ShouldBe(16);

        ColorCupIndividualPhasedOrchestrator.CutoffResolution second =
            ColorCupIndividualPhasedOrchestrator.ResolveCutoffFromTotals(totals, before);
        second.QualifierAthleteIds.ShouldBe(first.QualifierAthleteIds.ToList());
        second.RngAfterCutoff.ShouldBe(first.RngAfterCutoff);
    }

    [Fact]
    public void Cutoff_TiedAtSixteen_ConsumesRngOnceAndIsDeterministic()
    {
        List<StageAthleteTotals> totals = BuildTiedCutoffTotals();
        Pcg32State before = new(999UL, 1001UL);
        ColorCupIndividualPhasedOrchestrator.CutoffResolution first =
            ColorCupIndividualPhasedOrchestrator.ResolveCutoffFromTotals(totals, before);
        first.TieDrawConsumed.ShouldBeTrue();
        first.RngAfterCutoff.ShouldNotBe(before);
        first.QualifierAthleteIds.Count.ShouldBe(16);

        ColorCupIndividualPhasedOrchestrator.CutoffResolution second =
            ColorCupIndividualPhasedOrchestrator.ResolveCutoffFromTotals(totals, before);
        second.QualifierAthleteIds.ShouldBe(first.QualifierAthleteIds.ToList());
        second.RngAfterCutoff.ShouldBe(first.RngAfterCutoff);
        second.TieDrawConsumed.ShouldBeTrue();
    }

    [Fact]
    public void FinalRoster_RecoveredFromPayload_NotRerolled()
    {
        List<StageAthleteTotals> totals = BuildTiedCutoffTotals();
        ColorCupIndividualPhasedOrchestrator.CutoffResolution cutoff =
            ColorCupIndividualPhasedOrchestrator.ResolveCutoffFromTotals(totals, new Pcg32State(7UL, 9UL));
        cutoff.TieDrawConsumed.ShouldBeTrue();

        var placements = cutoff.QualifierAthleteIds
            .Select((id, index) => new RoundPayloadEntry(id, $"Athlete {id}", index + 1, 1000, 0, 1000, 0, 1000, 1, 1, 0))
            .ToList();
        var firstFinal = new ColorCupIndividualRoundPayloadDocument(
            1, 1, ColorCupIndividualFormat.PhasedFormatVersion, 1, 17, 7UL, 9UL, 11UL, 9UL, "checksum", placements);
        IReadOnlyList<int> recovered =
            ColorCupIndividualPhasedOrchestrator.RecoverQualifiersFromFinalPayload(firstFinal);
        recovered.OrderBy(id => id).ShouldBe(cutoff.QualifierAthleteIds.OrderBy(id => id).ToList());
    }

    [Fact]
    public void PhasedAccumulate_MergesCarryover_WithSplitAppearances()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<ColorCupIndividualRoundPayloadDocument> payloads = BuildPhasedPayloadsForAccumulate();
        IReadOnlyList<StageAthleteTotals> totals =
            ColorCupIndividualPhasedOrchestrator.AccumulatePhasedTotals(payloads, rules);
        totals.Count.ShouldBe(32);
        Dictionary<int, StageAthleteTotals> byId = totals.ToDictionary(t => t.AthleteId);
        for (int id = 1; id <= 32; id++)
        {
            StageAthleteTotals entry = byId[id];
            int sum = entry.RoundPlaceCounts.Sum();
            if (id <= 16)
            {
                sum.ShouldBe(32);
            }
            else
            {
                sum.ShouldBe(16);
            }
        }

        Pcg32V1 rng = Pcg32V1.Restore(new Pcg32State(42UL, 54UL));
        IReadOnlyList<StageRankedAthlete> ranked =
            ColorCupIndividualPhasedOrchestrator.RankPhasedTotals(totals, rng, rules, LeagueLevel.Feeder1);
        ranked.Count.ShouldBe(32);
        ranked.Select(r => r.StageRank).ShouldBe(Enumerable.Range(1, 32).ToList());
    }

    private static List<StageAthleteTotals> BuildDistinctTotals()
    {
        List<StageAthleteTotals> totals = new(32);
        for (int id = 1; id <= 32; id++)
        {
            List<int> counts = new(new int[32]);
            counts[(id - 1) % 32] = 16;
            totals.Add(new StageAthleteTotals(id, $"Athlete {id:D2}", (33 - id) * 1000, (33 - id) * 900, counts));
        }

        return totals;
    }

    private static List<StageAthleteTotals> BuildTiedCutoffTotals()
    {
        List<StageAthleteTotals> totals = new(32);
        for (int id = 1; id <= 32; id++)
        {
            int score;
            List<int> counts = new(new int[32]);
            if (id <= 15)
            {
                score = 100000 - id * 1000;
                counts[(id - 1) % 32] = 16;
            }
            else if (id <= 18)
            {
                score = 50000;
                counts[0] = 8;
                counts[1] = 8;
            }
            else
            {
                score = 40000 - id * 100;
                counts[(id - 1) % 32] = 16;
            }

            totals.Add(new StageAthleteTotals(id, $"Athlete {id:D2}", score, score - 100, counts));
        }

        return totals;
    }

    private static List<ColorCupIndividualRoundPayloadDocument> BuildPhasedPayloadsForAccumulate()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        List<ColorCupIndividualRoundPayloadDocument> payloads = new(32);
        for (int round = 1; round <= 16; round++)
        {
            List<RoundPayloadEntry> placements = new(32);
            for (int pos = 1; pos <= 32; pos++)
            {
                int id = pos;
                int basePoints = rules.ScoringTable[pos - 1] * RulesV1.FixedScale;
                placements.Add(new RoundPayloadEntry(id, $"Athlete {id:D2}", pos, basePoints, 0, basePoints, (round - 1) * basePoints, round * basePoints, pos, pos, 0));
            }

            payloads.Add(new ColorCupIndividualRoundPayloadDocument(
                1, 1, ColorCupIndividualFormat.PhasedFormatVersion, 1, round, (ulong)round, 1UL, (ulong)(round + 1), 1UL, "c", placements));
        }

        for (int round = 17; round <= 32; round++)
        {
            List<RoundPayloadEntry> placements = new(16);
            for (int pos = 1; pos <= 16; pos++)
            {
                int id = pos;
                int basePoints = rules.ScoringTable[pos - 1] * RulesV1.FixedScale;
                int before = 16 * basePoints + (round - 17) * basePoints;
                placements.Add(new RoundPayloadEntry(id, $"Athlete {id:D2}", pos, basePoints, 0, basePoints, before, before + basePoints, pos, pos, 0));
            }

            payloads.Add(new ColorCupIndividualRoundPayloadDocument(
                1, 1, ColorCupIndividualFormat.PhasedFormatVersion, 1, round, (ulong)round, 1UL, (ulong)(round + 1), 1UL, "c", placements));
        }

        return payloads;
    }

    private static async Task<List<string>> SnapshotAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<string> lines = [];
        lines.AddRange(await context.ColorCupIndividualRounds.OrderBy(e => e.RoundNumber)
            .Select(e => "round:" + e.PayloadJson).ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.ColorCupIndividualStandings.OrderBy(e => e.CupRank)
            .Select(e => $"standing:{e.SaveAthleteId}:{e.CupRank}:{e.CupScoreThousandths}:{e.Medal}").ToListAsync().ConfigureAwait(false));
        lines.AddRange(await context.Honours.OrderBy(e => e.Id)
            .Select(e => $"honour:{e.Kind}:{e.SaveAthleteId}").ToListAsync().ConfigureAwait(false));
        lines.Add("stories:" + await context.StoryEvents.CountAsync().ConfigureAwait(false));
        RngStateEntity rng = await context.RngStates.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        lines.Add($"rng:{rng.State}:{rng.Stream}");
        lines.Add("phase:" + (await context.SaveMetadata.SingleAsync().ConfigureAwait(false)).Phase);
        return lines;
    }
}
