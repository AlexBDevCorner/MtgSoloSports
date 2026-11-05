using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.GetColorCupIndividualProgress;
using MtgSoloSports.Features.Cups.GetColorCupIndividualResult;
using MtgSoloSports.Features.Cups.PlayColorCupIndividualRound;
using MtgSoloSports.Features.Cups.RunColorCupIndividual;
using MtgSoloSports.Features.Reports.CupEditionReport;
using MtgSoloSports.Features.Reports.SeasonDossier;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-121: display and export the confirmed 32-to-16 Color Cup cutoff.
/// New phased editions show 32 rounds with a round-16 top-16 boundary,
/// 16-person finals with full carryover and distinct 1-16 vs 17-32 tables.
/// Historical 16-round editions keep exact legacy output with no fabricated
/// cutoff or final rounds.
/// </summary>
public sealed class ColorCupIndividualProgressTests
{
    [Fact]
    public async Task CompletedPhased_ProgressShowsCutoffCarryoverAndDistinctTables()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup121-done-");
        try
        {
            RunColorCupIndividualResponse run = await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            run.Rounds.ShouldBe(32);
            run.IsPhased.ShouldBeTrue();
            run.FormatVersion.ShouldBe(ColorCupIndividualFormat.PhasedFormatVersion);
            run.CutoffRank.ShouldBe(16);
            run.QualifierAthleteIds!.Count.ShouldBe(16);
            run.EliminatedAthleteIds!.Count.ShouldBe(16);

            GetColorCupIndividualResultResponse result = await new GetColorCupIndividualResultHandler(store).HandleAsync(saveId);
            result.Rounds.ShouldBe(32);
            result.IsPhased.ShouldBeTrue();
            result.FormatVersion.ShouldBe(ColorCupIndividualFormat.PhasedFormatVersion);
            result.QualifierAthleteIds!.Count.ShouldBe(16);
            result.EliminatedAthleteIds!.Count.ShouldBe(16);
            result.QualifyingOrderAthleteIds!.Count.ShouldBe(32);
            result.Standings.Count.ShouldBe(32);
            result.Standings.Where(m => m.CupRank <= 16).ShouldAllBe(m => m.QualifiedForFinal == true);
            result.Standings.Where(m => m.CupRank > 16).ShouldAllBe(m => m.QualifiedForFinal == false);
            result.Standings.Where(m => m.CupRank <= 16).ShouldAllBe(m => m.QualifyingRank.HasValue && m.QualifyingScoreThousandths.HasValue && m.FinalScoreThousandths.HasValue);
            result.Standings.Where(m => m.CupRank > 16).ShouldAllBe(m => m.QualifyingRank.HasValue && m.FinalScoreThousandths == 0);

            GetColorCupIndividualProgressResponse progress =
                await new GetColorCupIndividualProgressHandler(store).HandleAsync(saveId);
            progress.FormatVersion.ShouldBe(ColorCupIndividualFormat.PhasedFormatVersion);
            progress.IsPhased.ShouldBeTrue();
            progress.TotalRounds.ShouldBe(32);
            progress.RoundsPlayed.ShouldBe(32);
            progress.IsComplete.ShouldBeTrue();
            progress.CutoffRank.ShouldBe(16);
            progress.QualifierAthleteIds!.Count.ShouldBe(16);
            progress.EliminatedAthleteIds!.Count.ShouldBe(16);
            progress.Standings.Count.ShouldBe(32);
            progress.Standings.Take(16).ShouldAllBe(m => m.AdvancingStatus == "Qualified");
            progress.Standings.Skip(16).ShouldAllBe(m => m.AdvancingStatus == "Eliminated");
            progress.Carryover.ShouldNotBeNull();
            progress.Carryover!.Count.ShouldBe(16);
            progress.Carryover.ShouldAllBe(c => c.Matches);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task QualifyingPhase_ProgressShowsProvisionalTop16()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup121-qual-");
        try
        {
            PlayColorCupIndividualRoundHandler step = new(store);
            for (int i = 0; i < 5; i++)
            {
                PlayColorCupIndividualRoundResponse played = await step.HandleAsync(saveId);
                played.RoundsPlayed.ShouldBe(i + 1);
                played.IsComplete.ShouldBeFalse();
            }

            GetColorCupIndividualProgressResponse progress =
                await new GetColorCupIndividualProgressHandler(store).HandleAsync(saveId);
            progress.IsPhased.ShouldBeTrue();
            progress.TotalRounds.ShouldBe(32);
            progress.RoundsPlayed.ShouldBe(5);
            progress.IsComplete.ShouldBeFalse();
            progress.IsProvisionalCutoff.ShouldBeTrue();
            progress.QualifierAthleteIds!.Count.ShouldBe(16);
            progress.EliminatedAthleteIds!.Count.ShouldBe(16);
            progress.Standings.Count.ShouldBe(32);
            progress.Standings.Take(16).ShouldAllBe(m => m.AdvancingStatus == "ProvisionalQualified");
            progress.Standings.Skip(16).ShouldAllBe(m => m.AdvancingStatus == "ProvisionalEliminated");
            progress.Carryover.ShouldBeNull();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FinalPhase_ProgressShowsSixteenActiveWithContinuity()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup121-final-");
        try
        {
            PlayColorCupIndividualRoundHandler step = new(store);
            for (int i = 0; i < 17; i++)
            {
                PlayColorCupIndividualRoundResponse played = await step.HandleAsync(saveId);
                played.RoundsPlayed.ShouldBe(i + 1);
            }

            GetColorCupIndividualProgressResponse progress =
                await new GetColorCupIndividualProgressHandler(store).HandleAsync(saveId);
            progress.IsPhased.ShouldBeTrue();
            progress.RoundsPlayed.ShouldBe(17);
            progress.IsComplete.ShouldBeFalse();
            progress.IsProvisionalCutoff.ShouldBeFalse();
            progress.QualifierAthleteIds!.Count.ShouldBe(16);
            progress.Standings.Count.ShouldBe(32);
            progress.Standings.Take(16).ShouldAllBe(m => m.AdvancingStatus == "Qualified");
            progress.Standings.Skip(16).ShouldAllBe(m => m.AdvancingStatus == "Eliminated");
            progress.Carryover.ShouldNotBeNull();
            progress.Carryover!.Count.ShouldBe(16);
            progress.Carryover.ShouldAllBe(c => c.Matches);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LegacyCompleted_ProgressAndResultHaveNoCutoff()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup121-legacy-");
        try
        {
            await new PlayColorCupIndividualRoundHandler(store).HandleAsync(saveId);
            await RewriteEditionToLegacyAsync(store, saveId);
            RunColorCupIndividualResponse run = await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            run.Rounds.ShouldBe(16);
            run.IsPhased.ShouldBeFalse();
            run.QualifierAthleteIds.ShouldBeNull();

            GetColorCupIndividualResultResponse result = await new GetColorCupIndividualResultHandler(store).HandleAsync(saveId);
            result.Rounds.ShouldBe(16);
            result.IsPhased.ShouldBeFalse();
            result.CutoffRank.ShouldBeNull();
            result.QualifierAthleteIds.ShouldBeNull();
            result.Standings.ShouldAllBe(m => m.QualifyingRank == null && m.QualifiedForFinal == null);

            GetColorCupIndividualProgressResponse progress =
                await new GetColorCupIndividualProgressHandler(store).HandleAsync(saveId);
            progress.IsPhased.ShouldBeFalse();
            progress.TotalRounds.ShouldBe(16);
            progress.RoundsPlayed.ShouldBe(16);
            progress.IsComplete.ShouldBeTrue();
            progress.CutoffRank.ShouldBeNull();
            progress.QualifierAthleteIds.ShouldBeNull();
            progress.Carryover.ShouldBeNull();
            progress.Standings.Count.ShouldBe(32);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CupEditionReport_PhasedShowsCutoffAndLegacyStaysClean()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup121-report-");
        try
        {
            await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            GetCupEditionReportHandler handler = new(store);

            GetCupEditionReportHandler.ReportFile md = await handler.HandleAsync(saveId, "color", 1, "md");
            string body = System.Text.Encoding.UTF8.GetString(md.Content);
            body.ShouldContain("Format: phased");
            body.ShouldContain("Cutoff: top 16 qualify");
            body.ShouldContain("Finalists — ranks 1–16");
            body.ShouldContain("Eliminated at the cutoff — ranks 17–32");
            body.ShouldContain("100% carryover");

            GetCupEditionReportHandler.ReportFile json = await handler.HandleAsync(saveId, "color", 1, "json");
            using JsonDocument doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(json.Content));
            doc.RootElement.TryGetProperty("colorIndividual", out JsonElement individual).ShouldBeTrue();
            individual.GetProperty("isPhased").GetBoolean().ShouldBeTrue();
            individual.GetProperty("rounds").GetInt32().ShouldBe(32);
            individual.GetProperty("qualifierAthleteIds").GetArrayLength().ShouldBe(16);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SeasonDossier_PhasedIncludesCutoffForDrive()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkColorCupSelectedAsync("mtgsolosports-cup121-dossier-");
        try
        {
            await new RunColorCupIndividualHandler(store).HandleAsync(saveId);
            using var context = store.OpenDbContext(saveId);
            var season = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1);
            GetCupEditionReportHandler.ReportData data =
                await GetCupEditionReportHandler.LoadColorAsync(context, saveId, season, CancellationToken.None);
            IndividualCupSummary? summary = SeasonDossierCupQueries.MapIndividual(data.ColorIndividual);
            summary.ShouldNotBeNull();
            summary!.IsPhased.ShouldBeTrue();
            summary.TotalRounds.ShouldBe(32);
            summary.CutoffRank.ShouldBe(16);
            summary.QualifierCount.ShouldBe(16);
            summary.EliminatedCount.ShouldBe(16);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task RewriteEditionToLegacyAsync(MtgSoloSports.Persistence.Saves.SaveStore store, Guid saveId)
    {
        using var context = store.OpenDbContext(saveId);
        var rows = await context.ColorCupIndividualRounds.ToListAsync().ConfigureAwait(false);
        foreach (var row in rows)
        {
            var payload = ColorCupIndividualRoundPayloadDocument.FromStored(row.PayloadJson);
            var legacy = payload with { FormatVersion = ColorCupIndividualFormat.LegacyFormatVersion };
            row.FormatVersion = ColorCupIndividualFormat.LegacyFormatVersion;
            row.PayloadJson = legacy.ToStored();
            row.PayloadChecksum = legacy.Checksum;
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }
}
