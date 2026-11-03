using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Cups.CompleteChaosCup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Features.Reports;
using MtgSoloSports.Features.Reports.CupEditionReport;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.Tests.Features;
using MtgSoloSports.Tests.Features.Cups;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

/// <summary>
/// MSS-114: Chaos Cup edition dossier as versioned JSON/Markdown.
/// Incidental Season 1 history is forked; the Chaos field selection, bulk
/// completion and every dossier read below still execute. The full-tournament
/// case simulates the whole Cup because the six-stage dossier IS the subject.
/// </summary>
public sealed class ChaosDossierReportTests
{
    [Fact]
    public async Task PartialDossier_HonestStatus_NoPrematureWinner_DeterministicReadOnly()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-dossier-partial-");
        try
        {
            (long rngBefore, long streamBefore, int roundsBefore) = await LoadRngAndRoundsAsync(store, saveId);
            GetCupEditionReportHandler handler = new(store);

            GetCupEditionReportHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, "chaos", 1, "json");
            jsonFile.ContentType.ShouldBe("application/json; charset=utf-8");
            jsonFile.FileName.ShouldBe("cup-chaos-season-1-edition.json");
            string jsonBody = Encoding.UTF8.GetString(jsonFile.Content);
            using JsonDocument doc = JsonDocument.Parse(jsonBody);
            AssertEnvelope(doc, isComplete: false);
            AssertNoGlobalRank(jsonBody);
            AssertFieldBreakdown(doc);

            string mdBody = await LoadMarkdownAsync(handler, saveId);
            AssertMarkdownPartial(mdBody);

            // Deterministic: second read matches modulo generatedUtc, RNG untouched.
            GetCupEditionReportHandler.ReportFile jsonSecond = await handler.HandleAsync(saveId, "CHAOS", 1, "json");
            Normalize(jsonBody).ShouldBe(Normalize(Encoding.UTF8.GetString(jsonSecond.Content)));
            (long rngAfter, long streamAfter, int roundsAfter) = await LoadRngAndRoundsAsync(store, saveId);
            rngAfter.ShouldBe(rngBefore);
            streamAfter.ShouldBe(streamBefore);
            roundsAfter.ShouldBe(roundsBefore);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CompleteDossier_CoversEachEntrantOnce_WithOfficialFinalAndCutoffs()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-dossier-complete-");
        try
        {
            await new CompleteChaosCupHandler(store).CompleteCupAsync(saveId, sourceSeasonNumber: 1);
            GetCupEditionReportHandler handler = new(store);

            GetCupEditionReportHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, "chaos", 1, "json");
            string jsonBody = Encoding.UTF8.GetString(jsonFile.Content);
            using JsonDocument doc = JsonDocument.Parse(jsonBody);
            AssertEnvelope(doc, isComplete: true);
            AssertNoGlobalRank(jsonBody);
            await AssertCompleteMatchesPersistedAsync(store, saveId, doc);

            string mdBody = await LoadMarkdownAsync(handler, saveId);
            AssertMarkdownComplete(mdBody, doc);

            GetCupEditionReportHandler.ReportFile mdFile = await handler.HandleAsync(saveId, "chaos", 1, "md");
            mdFile.ContentType.ShouldBe("text/markdown; charset=utf-8");
            mdFile.FileName.ShouldBe("cup-chaos-season-1-edition.md");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ChaosDossierRoutes_ReturnOk_WithVersionedSchema_WithoutMutating()
    {
        var (store, root, saveId) = await PrepareFieldAsync("mtgsolosports-chaos-dossier-http-");
        try
        {
            (long rngBefore, long streamBefore, int roundsBefore) = await LoadRngAndRoundsAsync(store, saveId);
            string factoryRoot = Path.Combine(Path.GetTempPath(), "mtgsolosports-chaos-dossier-api-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(factoryRoot);
            string savesRoot = Path.Combine(factoryRoot, "saves");
            Directory.CreateDirectory(savesRoot);
            WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.Configure<SaveStorageOptions>(o => o.SavesRoot = savesRoot);
                    services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(factoryRoot, "catalog.db"));
                }));
            try
            {
                string source = store.GetSaveFilePath(saveId);
                string destination = SaveFileNaming.GetSaveFilePath(savesRoot, saveId);
                await TestSaveStores.CopySaveFileLockedAsync(store, saveId, source, destination);
                using HttpClient client = factory.CreateClient();
                await AssertChaosDossierRoutesAsync(client, saveId);
            }
            finally
            {
                factory.Dispose();
                SqliteConnection.ClearAllPools();
                Directory.Delete(factoryRoot, recursive: true);
            }

            (long rngAfter, long streamAfter, int roundsAfter) = await LoadRngAndRoundsAsync(store, saveId);
            rngAfter.ShouldBe(rngBefore);
            streamAfter.ShouldBe(streamBefore);
            roundsAfter.ShouldBe(roundsBefore);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task<string> LoadMarkdownAsync(GetCupEditionReportHandler handler, Guid saveId)
    {
        GetCupEditionReportHandler.ReportFile mdFile = await handler.HandleAsync(saveId, "chaos", 1, "md").ConfigureAwait(false);
        string mdBody = Encoding.UTF8.GetString(mdFile.Content);
        mdBody.ShouldContain("# Chaos Cup edition dossier — Season 1");
        mdBody.ShouldContain("fixed-point thousandths");
        mdBody.ShouldContain("isolated per group");
        mdBody.ShouldContain("Career Leaders");
        mdBody.ShouldContain("no global numeric rank");
        return mdBody;
    }

    private static void AssertEnvelope(JsonDocument doc, bool isComplete)
    {
        doc.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(ReportContract.SchemaVersion);
        doc.RootElement.GetProperty("reportType").GetString().ShouldBe(ReportContract.ChaosCupDossierReportType);
        doc.RootElement.GetProperty("scope").GetProperty("cup").GetString().ShouldBe("Chaos");
        doc.RootElement.GetProperty("scope").GetProperty("sourceSeasonNumber").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("snapshot").GetProperty("state").GetString().ShouldBe(isComplete ? "Completed" : "InProgress");
        doc.RootElement.GetProperty("snapshot").GetProperty("isComplete").GetBoolean().ShouldBe(isComplete);
        doc.RootElement.GetProperty("snapshot").GetProperty("fieldSize").GetInt32().ShouldBe(ChaosCupFormat.FieldSize);
        doc.RootElement.GetProperty("snapshot").GetProperty("rulesVersion").GetInt32().ShouldBeGreaterThan(0);
        doc.RootElement.GetProperty("snapshot").GetProperty("chaosFormatVersion").GetInt32().ShouldBe(ChaosCupFormat.ChaosFormatVersion);
        doc.RootElement.GetProperty("snapshot").GetProperty("editionChecksum").GetString().ShouldNotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("entrants").GetArrayLength().ShouldBe(ChaosCupFormat.FieldSize);
        doc.RootElement.GetProperty("paths").GetArrayLength().ShouldBe(ChaosCupFormat.FieldSize);
        doc.RootElement.GetProperty("stages").GetArrayLength().ShouldBe(ChaosCupFormat.StageCount);
    }

    private static void AssertNoGlobalRank(string jsonBody)
    {
        jsonBody.ShouldNotContain("globalRank");
        jsonBody.ShouldNotContain("globalPlace");
        using JsonDocument doc = JsonDocument.Parse(jsonBody);
        foreach (JsonElement path in doc.RootElement.GetProperty("paths").EnumerateArray())
        {
            path.TryGetProperty("globalRank", out _).ShouldBeFalse();
            path.TryGetProperty("globalPlace", out _).ShouldBeFalse();
            if (path.GetProperty("isFinalist").GetBoolean())
            {
                path.GetProperty("finalPlace").ValueKind.ShouldNotBe(JsonValueKind.Null);
                path.GetProperty("exitStage").GetString().ShouldBe(nameof(ChaosCupFormat.ChaosExitStage.Finalist));
            }
            else
            {
                path.GetProperty("finalPlace").ValueKind.ShouldBe(JsonValueKind.Null);
            }

            if (path.GetProperty("isEliminated").GetBoolean())
            {
                path.GetProperty("exitStage").GetString().ShouldNotBeNullOrWhiteSpace();
                path.GetProperty("eliminatedStageIndex").ValueKind.ShouldNotBe(JsonValueKind.Null);
            }
        }
    }

    private static void AssertFieldBreakdown(JsonDocument doc)
    {
        JsonElement breakdown = doc.RootElement.GetProperty("fieldBreakdown");
        breakdown.GetProperty("total").GetInt32().ShouldBe(ChaosCupFormat.FieldSize);
        breakdown.GetProperty("byColor").GetArrayLength().ShouldBe(8);
        foreach (JsonElement bucket in breakdown.GetProperty("byColor").EnumerateArray())
        {
            bucket.GetProperty("count").GetInt32().ShouldBe(ChaosCupFormat.EntrantsPerColor);
        }

        breakdown.GetProperty("byDivision").GetArrayLength().ShouldBe(5);
        int divisionTotal = 0;
        foreach (JsonElement bucket in breakdown.GetProperty("byDivision").EnumerateArray())
        {
            divisionTotal = checked(divisionTotal + bucket.GetProperty("count").GetInt32());
        }

        divisionTotal.ShouldBe(ChaosCupFormat.FieldSize);
    }

    private static void AssertMarkdownPartial(string mdBody)
    {
        mdBody.ShouldContain("no official winner");
        mdBody.ShouldContain("The Final has not finished yet");
        mdBody.ShouldContain("NotDrawn");
        mdBody.ShouldContain("First draw: exactly 4 per sporting color");
        mdBody.ShouldContain("Later draws: pure random");
        mdBody.ShouldContain("## Paths (1024 athletes, each exactly once)");
    }

    private static async Task AssertCompleteMatchesPersistedAsync(SaveStore store, Guid saveId, JsonDocument doc)
    {
        AssertCompleteEntrants(doc);
        AssertCompleteFinal(doc);
        AssertCompleteStages(doc);
        AssertCompletePaths(doc);
        await AssertFinalMatchesPersistedAsync(store, saveId, doc).ConfigureAwait(false);
    }

    private static void AssertCompleteEntrants(JsonDocument doc)
    {
        List<int> entrantIds = doc.RootElement.GetProperty("entrants").EnumerateArray()
            .Select(e => e.GetProperty("athleteId").GetInt32()).ToList();
        entrantIds.Count.ShouldBe(ChaosCupFormat.FieldSize);
        entrantIds.Distinct().Count().ShouldBe(ChaosCupFormat.FieldSize);
        entrantIds.ShouldBe(entrantIds.OrderBy(id => id).ToList());

        List<int> pathIds = doc.RootElement.GetProperty("paths").EnumerateArray()
            .Select(e => e.GetProperty("athleteId").GetInt32()).ToList();
        pathIds.Count.ShouldBe(ChaosCupFormat.FieldSize);
        pathIds.Distinct().Count().ShouldBe(ChaosCupFormat.FieldSize);
        pathIds.ShouldBe(pathIds.OrderBy(id => id).ToList());
        pathIds.ShouldBe(entrantIds);
    }

    private static void AssertCompleteFinal(JsonDocument doc)
    {
        JsonElement final = doc.RootElement.GetProperty("final");
        final.ValueKind.ShouldNotBe(JsonValueKind.Null);
        List<int> places = final.GetProperty("standings").EnumerateArray()
            .Select(s => s.GetProperty("finalPlace").GetInt32()).ToList();
        places.ShouldBe(Enumerable.Range(1, ChaosCupFormat.FinalFieldSize).ToList());
        final.GetProperty("standings").EnumerateArray().First(s => s.GetProperty("finalPlace").GetInt32() == 1)
            .GetProperty("athleteId").GetInt32().ShouldBe(doc.RootElement.GetProperty("snapshot").GetProperty("champion").GetProperty("athleteId").GetInt32());
    }

    private static void AssertCompleteStages(JsonDocument doc)
    {
        foreach (JsonElement stage in doc.RootElement.GetProperty("stages").EnumerateArray())
        {
            stage.GetProperty("isComplete").GetBoolean().ShouldBeTrue();
            stage.GetProperty("state").GetString().ShouldBe("Completed");
            AssertCompleteGroups(stage);
        }
    }

    private static void AssertCompleteGroups(JsonElement stage)
    {
        foreach (JsonElement group in stage.GetProperty("groups").EnumerateArray())
        {
            group.GetProperty("isComplete").GetBoolean().ShouldBeTrue();
            List<int> ranks = group.GetProperty("standings").EnumerateArray()
                .Select(s => s.GetProperty("groupRank").GetInt32()).ToList();
            ranks.ShouldBe(Enumerable.Range(1, ChaosCupFormat.GroupSize).ToList());
            if (!stage.GetProperty("isFinal").GetBoolean())
            {
                AssertCutoff(group);
            }
        }
    }

    private static void AssertCutoff(JsonElement group)
    {
        JsonElement cutoff = group.GetProperty("cutoff");
        cutoff.ValueKind.ShouldNotBe(JsonValueKind.Null);
        cutoff.GetProperty("p16").GetProperty("groupRank").GetInt32().ShouldBe(16);
        cutoff.GetProperty("p17").GetProperty("groupRank").GetInt32().ShouldBe(17);
        cutoff.GetProperty("gapThousandths").GetInt32().ShouldBe(
            cutoff.GetProperty("p16").GetProperty("groupScoreThousandths").GetInt32()
            - cutoff.GetProperty("p17").GetProperty("groupScoreThousandths").GetInt32());
        cutoff.GetProperty("isOfficial").GetBoolean().ShouldBeTrue();
    }

    private static void AssertCompletePaths(JsonDocument doc)
    {
        int finalists = 0;
        foreach (JsonElement path in doc.RootElement.GetProperty("paths").EnumerateArray())
        {
            path.GetProperty("stages").GetArrayLength().ShouldBeGreaterThan(0);
            if (path.GetProperty("isFinalist").GetBoolean())
            {
                finalists++;
                path.GetProperty("stages").GetArrayLength().ShouldBe(ChaosCupFormat.StageCount);
            }
            else
            {
                path.GetProperty("isEliminated").GetBoolean().ShouldBeTrue();
            }
        }

        finalists.ShouldBe(ChaosCupFormat.FinalFieldSize);
    }

    private static async Task AssertFinalMatchesPersistedAsync(SaveStore store, Guid saveId, JsonDocument doc)
    {
        JsonElement final = doc.RootElement.GetProperty("final");
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> persistedFinal = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == ChaosCupFormat.StageCount - 1)
            .OrderBy(e => e.GroupRank)
            .ToListAsync().ConfigureAwait(false);
        persistedFinal.Count.ShouldBe(ChaosCupFormat.FinalFieldSize);
        List<int> jsonFinalIds = final.GetProperty("standings").EnumerateArray()
            .OrderBy(s => s.GetProperty("finalPlace").GetInt32())
            .Select(s => s.GetProperty("athleteId").GetInt32()).ToList();
        jsonFinalIds.ShouldBe(persistedFinal.Select(e => e.SaveAthleteId).ToList());
    }

    private static void AssertMarkdownComplete(string mdBody, JsonDocument doc)
    {
        mdBody.ShouldContain("Official Final — P1..P32");
        mdBody.ShouldContain("Cutoff (official)");
        mdBody.ShouldContain("## Paths (1024 athletes, each exactly once)");
        string champion = doc.RootElement.GetProperty("snapshot").GetProperty("champion").GetProperty("name").GetString()!;
        mdBody.ShouldContain(champion);
        mdBody.ShouldNotContain("no official winner");
        mdBody.ShouldNotContain("The Final has not finished yet");
    }

    private static async Task AssertChaosDossierRoutesAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage json = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/editions/1/report?format=json").ConfigureAwait(false);
        json.StatusCode.ShouldBe(HttpStatusCode.OK);
        json.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        json.Content.Headers.ContentDisposition?.DispositionType.ShouldBe("attachment");
        string body = await json.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(ReportContract.SchemaVersion);
        doc.RootElement.GetProperty("reportType").GetString().ShouldBe(ReportContract.ChaosCupDossierReportType);
        doc.RootElement.GetProperty("entrants").GetArrayLength().ShouldBe(ChaosCupFormat.FieldSize);

        using HttpResponseMessage md = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/editions/1/report?format=md").ConfigureAwait(false);
        md.StatusCode.ShouldBe(HttpStatusCode.OK);
        md.Content.Headers.ContentType?.MediaType.ShouldBe("text/markdown");
        string mdBody = await md.Content.ReadAsStringAsync().ConfigureAwait(false);
        mdBody.ShouldContain("# Chaos Cup edition dossier");

        using HttpResponseMessage missing = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/editions/9/report?format=json").ConfigureAwait(false);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using HttpResponseMessage badFormat = await client.GetAsync($"/api/saves/{saveId:D}/cups/chaos/editions/1/report?format=pdf").ConfigureAwait(false);
        badFormat.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using HttpResponseMessage badSave = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/cups/chaos/editions/1/report?format=json").ConfigureAwait(false);
        badSave.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string Normalize(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        using var stream = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        WriteNormalized(writer, doc.RootElement);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNormalized(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (string.Equals(prop.Name, "generatedUtc", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writer.WritePropertyName(prop.Name);
                    WriteNormalized(writer, prop.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteNormalized(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static async Task<(long State, long Stream, int Rounds)> LoadRngAndRoundsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        int rounds = await context.ChaosCupRounds.AsNoTracking().CountAsync(e => e.SourceSeasonId == source.Id).ConfigureAwait(false);
        return (rng.State, rng.Stream, rounds);
    }

    private static async Task<(SaveStore Store, string Root, Guid SaveId)> PrepareFieldAsync(string prefix)
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync(prefix).ConfigureAwait(false);
        await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1).ConfigureAwait(false);
        return (store, root, saveId);
    }
}
