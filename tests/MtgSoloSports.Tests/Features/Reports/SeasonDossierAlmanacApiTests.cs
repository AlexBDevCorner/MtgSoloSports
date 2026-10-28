using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

public sealed class SeasonDossierAlmanacApiTests
{
    [Fact]
    public async Task SeasonDossier_Season1_Complete_MarkdownAndJsonAgree()
    {
        var (factory, root) = CreateFactory();
        string savesRoot = Path.Combine(root, "saves");
        Directory.CreateDirectory(savesRoot);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(savesRoot);
            using HttpClient client = factory.CreateClient();
            (long rngBefore, long streamBefore, int standingsBefore) = await ReadRngAndStandingsAsync(root, saveId);
            string jsonBody = await GetDossierJsonAsync(client, saveId, 1);
            await AssertDossierSeason1Async(client, saveId, jsonBody);
            await AssertDossierDeterministicAsync(client, saveId, 1, jsonBody);
            (long rngAfter, long streamAfter, int standingsAfter) = await ReadRngAndStandingsAsync(root, saveId);
            rngAfter.ShouldBe(rngBefore);
            streamAfter.ShouldBe(streamBefore);
            standingsAfter.ShouldBe(standingsBefore);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SeasonDossier_Season2_Provisional_PendingStates()
    {
        var (factory, root) = CreateFactory();
        string savesRoot = Path.Combine(root, "saves");
        Directory.CreateDirectory(savesRoot);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(savesRoot);
            using HttpClient client = factory.CreateClient();
            await CompleteOneStageAsync(client, saveId);
            string jsonBody = await GetDossierJsonAsync(client, saveId, 2);
            using JsonDocument json = JsonDocument.Parse(jsonBody);
            json.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
            json.RootElement.GetProperty("reportType").GetString().ShouldBe("season-dossier");
            json.RootElement.GetProperty("scope").GetProperty("seasonNumber").GetInt32().ShouldBe(2);
            json.RootElement.GetProperty("snapshot").GetProperty("status").GetString().ShouldBe("provisional");
            json.RootElement.GetProperty("snapshot").GetProperty("isFinal").GetBoolean().ShouldBeFalse();
            json.RootElement.GetProperty("snapshot").GetProperty("completedStages").GetInt32().ShouldBe(1);
            JsonElement table = json.RootElement.GetProperty("superleague").GetProperty("table");
            table.GetArrayLength().ShouldBe(32);
            var ranks = table.EnumerateArray().Select(r => r.GetProperty("rank").GetInt32()).ToList();
            ranks.ShouldBe(Enumerable.Range(1, 32).ToList());
            foreach (JsonElement row in table.EnumerateArray())
            {
                row.GetProperty("zone").ValueKind.ShouldBe(JsonValueKind.Null);
            }

            json.RootElement.GetProperty("colorCup").GetProperty("state").GetString().ShouldBe("NotHeld");
            json.RootElement.GetProperty("typeCup").GetProperty("state").GetString().ShouldBe("NotHeld");
            json.RootElement.GetProperty("history").GetProperty("previousSeasonNumber").GetInt32().ShouldBe(1);
            JsonElement previous = json.RootElement.GetProperty("history").GetProperty("previousChampion");
            previous.ValueKind.ShouldNotBe(JsonValueKind.Null);
            previous.GetProperty("athleteId").GetInt32().ShouldBeGreaterThan(0);
            using HttpResponseMessage md = await client.GetAsync($"/api/saves/{saveId:D}/reports/season/2?format=md");
            md.StatusCode.ShouldBe(HttpStatusCode.OK);
            string mdBody = await md.Content.ReadAsStringAsync();
            mdBody.ShouldContain("# Season dossier — Season 2");
            mdBody.ShouldContain("provisional");
            mdBody.ShouldContain("pending");
            mdBody.ShouldContain(previous.GetProperty("athleteName").GetString()!);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UniverseAlmanac_AllTime_Curated()
    {
        var (factory, root) = CreateFactory();
        string savesRoot = Path.Combine(root, "saves");
        Directory.CreateDirectory(savesRoot);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(savesRoot);
            using HttpClient client = factory.CreateClient();
            (long rngBefore, long streamBefore, int standingsBefore) = await ReadRngAndStandingsAsync(root, saveId);
            string jsonBody = await GetAlmanacJsonAsync(client, saveId);
            await AssertAlmanacAsync(client, saveId, jsonBody);
            await AssertAlmanacDeterministicAsync(client, saveId, jsonBody);
            (long rngAfter, long streamAfter, int standingsAfter) = await ReadRngAndStandingsAsync(root, saveId);
            rngAfter.ShouldBe(rngBefore);
            streamAfter.ShouldBe(streamBefore);
            standingsAfter.ShouldBe(standingsBefore);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DossierAndAlmanac_BadIdentifiers()
    {
        var (factory, root) = CreateFactory();
        string savesRoot = Path.Combine(root, "saves");
        Directory.CreateDirectory(savesRoot);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(savesRoot);
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage noSeason = await client.GetAsync($"/api/saves/{saveId:D}/reports/season/99?format=json");
            noSeason.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            using HttpResponseMessage badFormat = await client.GetAsync($"/api/saves/{saveId:D}/reports/season/1?format=csv");
            badFormat.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            using HttpResponseMessage badSeason = await client.GetAsync($"/api/saves/{saveId:D}/reports/season/0?format=json");
            badSeason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            using HttpResponseMessage badAlmanacFormat = await client.GetAsync($"/api/saves/{saveId:D}/reports/almanac?format=pdf");
            badAlmanacFormat.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            using HttpResponseMessage badSave = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/reports/almanac?format=json");
            badSave.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertDossierSeason1Async(HttpClient client, Guid saveId, string jsonBody)
    {
        (string championName, int championPoints) = AssertDossierSeason1Json(jsonBody);
        await AssertDossierSeason1MarkdownAsync(client, saveId, championName, championPoints).ConfigureAwait(false);
    }

    private static (string ChampionName, int ChampionPoints) AssertDossierSeason1Json(string jsonBody)
    {
        using JsonDocument json = JsonDocument.Parse(jsonBody);
        json.RootElement.GetProperty("reportType").GetString().ShouldBe("season-dossier");
        json.RootElement.GetProperty("scope").GetProperty("seasonNumber").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("snapshot").GetProperty("status").GetString().ShouldBe("final");
        json.RootElement.GetProperty("snapshot").GetProperty("completedStages").GetInt32().ShouldBe(32);
        JsonElement table = json.RootElement.GetProperty("superleague").GetProperty("table");
        table.GetArrayLength().ShouldBe(32);
        var ranks = table.EnumerateArray().Select(r => r.GetProperty("rank").GetInt32()).ToList();
        ranks.ShouldBe(Enumerable.Range(1, 32).ToList());
        JsonElement champion = table.EnumerateArray().First(r => r.GetProperty("rank").GetInt32() == 1);
        champion.GetProperty("isChampion").GetBoolean().ShouldBeTrue();
        string championName = champion.GetProperty("name").GetString()!;
        int championPoints = champion.GetProperty("championshipPointsThousandths").GetInt32();
        json.RootElement.GetProperty("stageProgression").GetArrayLength().ShouldBe(32);
        json.RootElement.GetProperty("feeders").GetArrayLength().ShouldBeGreaterThan(0);
        json.RootElement.GetProperty("colorCup").GetProperty("state").GetString().ShouldBe("Completed");
        json.RootElement.TryGetProperty("typeCup", out JsonElement typeCup).ShouldBeTrue();
        typeCup.GetProperty("state").GetString().ShouldBe("NotHeld");
        JsonElement colorIndividual = json.RootElement.GetProperty("colorCup").GetProperty("individual");
        colorIndividual.ValueKind.ShouldNotBe(JsonValueKind.Null);
        colorIndividual.GetProperty("championName").GetString().ShouldNotBeNullOrWhiteSpace();
        JsonElement colorTeam = json.RootElement.GetProperty("colorCup").GetProperty("team");
        colorTeam.ValueKind.ShouldNotBe(JsonValueKind.Null);
        JsonElement history = json.RootElement.GetProperty("history");
        history.GetProperty("championsLastFive").GetArrayLength().ShouldBeGreaterThan(0);
        JsonElement leaders = history.GetProperty("leaderCareers");
        leaders.GetArrayLength().ShouldBeGreaterThan(0);
        JsonElement records = json.RootElement.GetProperty("careerRecords");
        records.GetArrayLength().ShouldBe(13);
        return (championName, championPoints);
    }

    private static async Task AssertDossierSeason1MarkdownAsync(HttpClient client, Guid saveId, string championName, int championPoints)
    {
        using HttpResponseMessage md = await client.GetAsync($"/api/saves/{saveId:D}/reports/season/1?format=md").ConfigureAwait(false);
        md.StatusCode.ShouldBe(HttpStatusCode.OK);
        md.Content.Headers.ContentType?.MediaType.ShouldBe("text/markdown");
        string file = md.Content.Headers.ContentDisposition?.FileNameStar ?? md.Content.Headers.ContentDisposition?.FileName ?? string.Empty;
        file.ShouldContain("season-1-dossier");
        file.ShouldEndWith(".md");
        string mdBody = await md.Content.ReadAsStringAsync().ConfigureAwait(false);
        mdBody.ShouldContain("# Season dossier — Season 1");
        mdBody.ShouldContain("final table");
        mdBody.ShouldContain(championName);
        mdBody.ShouldContain(championPoints.ToString(System.Globalization.CultureInfo.InvariantCulture).Substring(0, 2));
        mdBody.ShouldContain("## Superleague");
        mdBody.ShouldContain("## Stage progression");
        mdBody.ShouldContain("## Color Cup");
        mdBody.ShouldContain("## Type Cup");
        mdBody.ShouldContain("not yet selected");
        mdBody.ShouldContain("## Historical perspective");
        mdBody.ShouldContain("## Official career records");
        mdBody.ShouldContain("stale");
    }

    private static async Task AssertAlmanacAsync(HttpClient client, Guid saveId, string jsonBody)
    {
        using JsonDocument json = JsonDocument.Parse(jsonBody);
        json.RootElement.GetProperty("reportType").GetString().ShouldBe("universe-almanac");
        json.RootElement.GetProperty("scope").GetProperty("seasonCount").GetInt32().ShouldBeGreaterThanOrEqualTo(2);
        JsonElement seasons = json.RootElement.GetProperty("seasons");
        seasons.GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);
        JsonElement podiums = json.RootElement.GetProperty("superleaguePodiums");
        podiums.GetArrayLength().ShouldBeGreaterThan(0);
        JsonElement feeders = json.RootElement.GetProperty("feederTitles");
        feeders.GetArrayLength().ShouldBeGreaterThan(0);
        JsonElement editions = json.RootElement.GetProperty("cupEditions");
        editions.GetArrayLength().ShouldBeGreaterThan(0);
        JsonElement colorTeams = json.RootElement.GetProperty("colorTeamTable");
        colorTeams.GetArrayLength().ShouldBe(8);
        JsonElement records = json.RootElement.GetProperty("careerRecords");
        records.GetArrayLength().ShouldBe(13);
        JsonElement leaders = json.RootElement.GetProperty("careerLeaders");
        leaders.GetProperty("weightsVersion").GetString().ShouldNotBeNullOrWhiteSpace();
        leaders.GetProperty("leaders").GetArrayLength().ShouldBeGreaterThan(0);
        JsonElement pointsLeaders = json.RootElement.GetProperty("superleaguePointsLeaders");
        pointsLeaders.ValueKind.ShouldBe(JsonValueKind.Array);
        using HttpResponseMessage md = await client.GetAsync($"/api/saves/{saveId:D}/reports/almanac?format=md").ConfigureAwait(false);
        md.StatusCode.ShouldBe(HttpStatusCode.OK);
        md.Content.Headers.ContentType?.MediaType.ShouldBe("text/markdown");
        string file = md.Content.Headers.ContentDisposition?.FileNameStar ?? md.Content.Headers.ContentDisposition?.FileName ?? string.Empty;
        file.ShouldContain("universe-almanac");
        file.ShouldEndWith(".md");
        string mdBody = await md.Content.ReadAsStringAsync().ConfigureAwait(false);
        mdBody.ShouldContain("# Universe almanac");
        mdBody.ShouldContain("## Seasons");
        mdBody.ShouldContain("## Superleague podiums");
        mdBody.ShouldContain("## Feeder champions");
        mdBody.ShouldContain("## Cup editions");
        mdBody.ShouldContain("## All-time Career Leaders");
        mdBody.ShouldContain("weighted career points");
        mdBody.ShouldContain("## Superleague points leaders");
        mdBody.ShouldContain("different statistic");
        mdBody.ShouldContain("stale");
    }

    private static async Task<string> GetDossierJsonAsync(HttpClient client, Guid saveId, int seasonNumber)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/reports/season/{seasonNumber}?format=json").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        string file = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? string.Empty;
        file.ShouldContain($"season-{seasonNumber}-dossier");
        file.ShouldEndWith(".json");
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        return body;
    }

    private static async Task<string> GetAlmanacJsonAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/reports/almanac?format=json").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        string file = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? string.Empty;
        file.ShouldContain("universe-almanac");
        file.ShouldEndWith(".json");
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        return body;
    }

    private static async Task AssertDossierDeterministicAsync(HttpClient client, Guid saveId, int seasonNumber, string jsonBody)
    {
        using HttpResponseMessage second = await client.GetAsync($"/api/saves/{saveId:D}/reports/season/{seasonNumber}?format=json").ConfigureAwait(false);
        string body2 = await second.Content.ReadAsStringAsync().ConfigureAwait(false);
        Normalize(jsonBody).ShouldBe(Normalize(body2));
    }

    private static async Task AssertAlmanacDeterministicAsync(HttpClient client, Guid saveId, string jsonBody)
    {
        using HttpResponseMessage second = await client.GetAsync($"/api/saves/{saveId:D}/reports/almanac?format=json").ConfigureAwait(false);
        string body2 = await second.Content.ReadAsStringAsync().ConfigureAwait(false);
        Normalize(jsonBody).ShouldBe(Normalize(body2));
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

    private static async Task CompleteOneStageAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null).ConfigureAwait(false);
        bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<(long Rng, long Stream, int Standings)> ReadRngAndStandingsAsync(string root, Guid saveId)
    {
        string path = Path.Combine(root, "saves", $"{saveId:N}.db");
        var options = new DbContextOptionsBuilder<SaveDbContext>().UseSqlite($"Data Source={path}").Options;
        using var context = new SaveDbContext(options);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        int standings = await context.SeasonStandings.AsNoTracking().CountAsync().ConfigureAwait(false);
        return (rng.State, rng.Stream, standings);
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-dossier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }
}
