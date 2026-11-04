using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.PlayColorCupIndividualRound;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.SimulationKernel.Stages;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-119 compatibility groundwork: legacy 16x32 editions stay readable and
/// replayable, already-started Cups fix their format at the first round, and
/// format-aware validation supports the future phased 16x32+16x16 shape without
/// activating its simulation (MSS-120).
/// </summary>
public sealed class ColorCupIndividualFormatCompatibilityTests
{
    [Fact]
    public async Task LegacyCompletedCup_ResolvesLegacyAndStaysReplayable()
    {
        // Incidental S1 + selection history is forked; the Cup run below still executes.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup119-legacy-");
        try
        {
            RunColorCupIndividualResponse response = await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            response.Rounds.ShouldBe(16);
            response.Standings.Count.ShouldBe(32);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            List<ColorCupIndividualRoundEntity> rows = await context.ColorCupIndividualRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.RoundNumber).ToListAsync();
            rows.Count.ShouldBe(16);
            rows.All(r => r.FormatVersion == ColorCupIndividualFormat.LegacyFormatVersion).ShouldBeTrue();

            List<ColorCupIndividualRoundPayloadDocument> payloads = rows
                .Select(r => ColorCupIndividualRoundPayloadDocument.FromStored(r.PayloadJson)).ToList();
            payloads.All(p => p.FormatVersion == ColorCupIndividualFormat.LegacyFormatVersion).ShouldBeTrue();
            payloads.All(p => p.Placements.Count == 32).ShouldBeTrue();

            ColorCupIndividualInvariants.ResolvePersistedFormat(rows).ShouldBe(ColorCupIndividualFormat.LegacyFormatVersion);
            ColorCupIndividualInvariants.ResolvePayloadFormat(payloads).ShouldBe(ColorCupIndividualFormat.LegacyFormatVersion);
            RunColorCupIndividualHandler.ResolveEditionFormat(rows, payloads).ShouldBe(ColorCupIndividualFormat.LegacyFormatVersion);

            // Historical read path still passes and replays without resimulation.
            GetColorCupIndividualResultResponse summary = await new GetColorCupIndividualResultHandler(store).HandleAsync(saveId);
            summary.Rounds.ShouldBe(16);
            summary.Standings.Count.ShouldBe(32);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LegacyPartialCup_FixesFormatAtStartAndRejectsMixedVersions()
    {
        // Incidental history forked; the two step rounds below still execute.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup119-partial-");
        try
        {
            PlayColorCupIndividualRoundHandler step = new(store);
            await step.HandleAsync(saveId);
            await step.HandleAsync(saveId);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            List<ColorCupIndividualRoundEntity> rows = await context.ColorCupIndividualRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.RoundNumber).ToListAsync();
            rows.Count.ShouldBe(2);
            List<ColorCupIndividualRoundPayloadDocument> payloads = rows
                .Select(r => ColorCupIndividualRoundPayloadDocument.FromStored(r.PayloadJson)).ToList();

            // Already-started Cups are unambiguously legacy.
            RunColorCupIndividualHandler.ResolveEditionFormat(rows, payloads).ShouldBe(ColorCupIndividualFormat.LegacyFormatVersion);

            // A partly played edition can never silently switch format.
            List<ColorCupIndividualRoundEntity> mixedRows = [rows[0], new ColorCupIndividualRoundEntity { FormatVersion = ColorCupIndividualFormat.PhasedFormatVersion }];
            Should.Throw<InvalidOperationException>(() => ColorCupIndividualInvariants.ResolvePersistedFormat(mixedRows));
            List<ColorCupIndividualRoundPayloadDocument> mixedPayloads = [payloads[0], payloads[0] with { FormatVersion = ColorCupIndividualFormat.PhasedFormatVersion }];
            Should.Throw<InvalidOperationException>(() => ColorCupIndividualInvariants.ResolvePayloadFormat(mixedPayloads));
            Should.Throw<InvalidOperationException>(() => RunColorCupIndividualHandler.ResolveEditionFormat(rows, mixedPayloads));

            var rules = await AdvanceRoundHandler.LoadRulesAsync(context, CancellationToken.None);

            // Legacy format rejects phased-only round numbers.
            ColorCupIndividualRoundPayloadDocument phasedRound17 = payloads[0] with
            {
                FormatVersion = ColorCupIndividualFormat.PhasedFormatVersion,
                RoundNumber = 17,
            };
            Should.Throw<InvalidOperationException>(() =>
                ColorCupIndividualInvariants.ValidateRoundForFormat(phasedRound17, rules, phasedRound17.RngBeforeState, phasedRound17.RngBeforeStream, ColorCupIndividualFormat.LegacyFormatVersion));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task OldPayloadWithoutFormatVersion_ReadsAsLegacy()
    {
        // Incidental history forked; the Cup run below still executes.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup119-oldjson-");
        try
        {
            RunColorCupIndividualResponse response = await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            response.Rounds.ShouldBe(16);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            ColorCupIndividualRoundEntity row = await context.ColorCupIndividualRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.RoundNumber).FirstAsync();
            ColorCupIndividualRoundPayloadDocument current = ColorCupIndividualRoundPayloadDocument.FromStored(row.PayloadJson);

            // Simulate a pre-MSS-119 payload: strip the new formatVersion property.
            string withoutFormat = StripFormatVersion(current.ToJson());
            withoutFormat.ShouldNotContain("formatVersion");
            ColorCupIndividualRoundPayloadDocument legacy = ColorCupIndividualRoundPayloadDocument.FromJson(withoutFormat);
            legacy.FormatVersion.ShouldBe(ColorCupIndividualFormat.LegacyFormatVersion);

            var rules = await AdvanceRoundHandler.LoadRulesAsync(context, CancellationToken.None);
            ColorCupIndividualInvariants.ValidateRound(legacy, rules, legacy.RngBeforeState, legacy.RngBeforeStream);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task PhasedRoundValidation_SupportsBothShapesAndRejectsWrongSizes()
    {
        // Incidental history forked; the Cup run provides authentic placements.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup119-shapes-");
        try
        {
            RunColorCupIndividualResponse response = await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            response.Standings.Count.ShouldBe(32);

            using SaveDbContext context = store.OpenDbContext(saveId);
            SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            ColorCupIndividualRoundEntity row = await context.ColorCupIndividualRounds.AsNoTracking()
                .Where(e => e.SourceSeasonId == source.Id).OrderBy(e => e.RoundNumber).FirstAsync();
            ColorCupIndividualRoundPayloadDocument qualifying = ColorCupIndividualRoundPayloadDocument.FromStored(row.PayloadJson);
            var rules = await AdvanceRoundHandler.LoadRulesAsync(context, CancellationToken.None);

            // Rounds 1-16 hold 32 placements in both formats.
            ColorCupIndividualInvariants.ValidateRoundForFormat(qualifying, rules, qualifying.RngBeforeState, qualifying.RngBeforeStream, ColorCupIndividualFormat.LegacyFormatVersion);
            ColorCupIndividualRoundPayloadDocument phasedQualifying = qualifying with { FormatVersion = ColorCupIndividualFormat.PhasedFormatVersion };
            ColorCupIndividualInvariants.ValidateRoundForFormat(phasedQualifying, rules, phasedQualifying.RngBeforeState, phasedQualifying.RngBeforeStream, ColorCupIndividualFormat.PhasedFormatVersion);

            // A 32-placement payload cannot satisfy a phased final round (expects 16).
            ColorCupIndividualRoundPayloadDocument wrongSize = qualifying with
            {
                FormatVersion = ColorCupIndividualFormat.PhasedFormatVersion,
                RoundNumber = 17,
            };
            InvalidOperationException ex = Should.Throw<InvalidOperationException>(() =>
                ColorCupIndividualInvariants.ValidateRoundForFormat(wrongSize, rules, wrongSize.RngBeforeState, wrongSize.RngBeforeStream, ColorCupIndividualFormat.PhasedFormatVersion));
            ex.Message.ShouldContain("exactly 16 placements");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public void PhasedCompletion_ExpectsSplitAppearances()
    {
        // Pure validation: finalists keep 100% of qualifying points (32 rounds),
        // eliminated athletes stop at the round-16 cutoff (16 rounds).
        (List<StageRankedAthlete> ranked, List<StageAthleteTotals> totals) = BuildSplitTotals();
        var rules = MtgSoloSports.SimulationKernel.Rules.RulesV1.CreateDefault();

        // Legacy still expects 16 for everyone.
        (List<StageRankedAthlete> legacyRanked, List<StageAthleteTotals> legacyTotals) = BuildLegacyTotals();
        ColorCupIndividualInvariants.ValidateCompletedCupForFormat(legacyRanked, legacyTotals, rules, ColorCupIndividualFormat.LegacyFormatVersion);

        // Phased split passes; legacy sums fail the phased expectation for finalists.
        ColorCupIndividualInvariants.ValidateCompletedCupForFormat(ranked, totals, rules, ColorCupIndividualFormat.PhasedFormatVersion);
        Should.Throw<InvalidOperationException>(() =>
            ColorCupIndividualInvariants.ValidateCompletedCupForFormat(legacyRanked, legacyTotals, rules, ColorCupIndividualFormat.PhasedFormatVersion));
    }

    [Fact]
    public async Task PhasedEdition_PersistsDistinctFormatWithoutDefaultRules()
    {
        // Incidental history forked; the single step round below still executes.
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup119-phased-");
        try
        {
            await new PlayColorCupIndividualRoundHandler(store).HandleAsync(saveId);

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                ColorCupIndividualRoundEntity row = await context.ColorCupIndividualRounds.SingleAsync(e => e.RoundNumber == 1);
                ColorCupIndividualRoundPayloadDocument payload = ColorCupIndividualRoundPayloadDocument.FromStored(row.PayloadJson);
                payload.Placements.Count.ShouldBe(32);

                // Future phased editions reuse the identical round-1 shape but
                // persist a distinct format tag without touching default rules.
                ColorCupIndividualRoundPayloadDocument phased = payload with { FormatVersion = ColorCupIndividualFormat.PhasedFormatVersion };
                row.FormatVersion = ColorCupIndividualFormat.PhasedFormatVersion;
                row.PayloadJson = phased.ToStored();
                row.PayloadChecksum = phased.Checksum;
                await context.SaveChangesAsync();
            }

            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                List<ColorCupIndividualRoundEntity> rows = await context.ColorCupIndividualRounds.AsNoTracking().ToListAsync();
                List<ColorCupIndividualRoundPayloadDocument> payloads = rows
                    .Select(r => ColorCupIndividualRoundPayloadDocument.FromStored(r.PayloadJson)).ToList();

                // Resolved from stored rows/payloads, never from runtime defaults.
                RunColorCupIndividualHandler.ResolveEditionFormat(rows, payloads).ShouldBe(ColorCupIndividualFormat.PhasedFormatVersion);

                var rules = await AdvanceRoundHandler.LoadRulesAsync(context, CancellationToken.None);
                rules.ColorCupIndividualRounds.ShouldBe(16);
                ColorCupIndividualInvariants.ValidateRound(payloads[0], rules, payloads[0].RngBeforeState, payloads[0].RngBeforeStream);
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static string StripFormatVersion(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "formatVersion", StringComparison.Ordinal))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static (List<StageRankedAthlete> Ranked, List<StageAthleteTotals> Totals) BuildLegacyTotals()
    {
        List<StageRankedAthlete> ranked = new(32);
        List<StageAthleteTotals> totals = new(32);
        for (int rank = 1; rank <= 32; rank++)
        {
            List<int> counts = new(new int[32]);
            counts[(rank - 1) % 32] = 16;
            ranked.Add(new StageRankedAthlete(rank, $"Athlete {rank}", rank, rank * 1000, rank * 900, 0, 0, counts, 0));
            totals.Add(new StageAthleteTotals(rank, $"Athlete {rank}", rank * 1000, rank * 900, counts));
        }

        return (ranked, totals);
    }

    private static (List<StageRankedAthlete> Ranked, List<StageAthleteTotals> Totals) BuildSplitTotals()
    {
        List<StageRankedAthlete> ranked = new(32);
        List<StageAthleteTotals> totals = new(32);
        for (int rank = 1; rank <= 32; rank++)
        {
            int expected = rank <= 16 ? 32 : 16;
            List<int> counts = new(new int[32]);
            counts[(rank - 1) % 32] = expected;
            ranked.Add(new StageRankedAthlete(rank, $"Athlete {rank}", rank, rank * 1000, rank * 900, 0, 0, counts, 0));
            totals.Add(new StageAthleteTotals(rank, $"Athlete {rank}", rank * 1000, rank * 900, counts));
        }

        return (ranked, totals);
    }
}
