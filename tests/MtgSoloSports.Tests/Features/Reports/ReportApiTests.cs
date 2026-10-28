using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

public sealed class ReportApiTests
{
    [Fact]
    public async Task AthleteReport_JsonAndMarkdownAgreeAndAreDeterministic()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Report Athlete", 2024UL, 3035UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);
            await CompleteOneStageAsync(client, saveId);
            int athleteId = await FirstStandingAthleteAsync(client, saveId, leagueId);
            (long rngBefore, long streamBefore, int standingsBefore) = await ReadRngAndStandingsAsync(root, saveId);
            string jsonBody = await GetAthleteJsonAsync(client, saveId, athleteId);
            string mdBody = await GetAthleteMarkdownAsync(client, saveId, athleteId, jsonBody);
            await AssertAthleteDeterministicAsync(client, saveId, athleteId, jsonBody);
            await AssertReadOnlyAsync(root, saveId, rngBefore, streamBefore, standingsBefore);
            _ = mdBody;
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AthleteReport_BadIdentifiers()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Report Bad", 11UL, 22UL);
            await AssertAthleteBadIdentifiersAsync(client, saveId);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StandingsReport_ProvisionalThenFinal_NoFallback()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Report Standings", 4242UL, 777UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);
            await CompleteOneStageAsync(client, saveId);
            await AssertProvisionalAsync(client, saveId, leagueId);
            await CompleteSeasonAsync(client, saveId);
            await AssertFinalAsync(client, saveId, leagueId);
            await AssertNoFallbackAsync(client, saveId, leagueId);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StandingsReport_HistoricalSeasonStaysPinned()
    {
        var (factory, root) = CreateFactory();
        string savesRoot = Path.Combine(root, "saves");
        Directory.CreateDirectory(savesRoot);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(savesRoot);
            using HttpClient client = factory.CreateClient();
            int season1League = await FirstLeagueIdForSeasonAsync(client, saveId, 1);
            int season2League = await FirstLeagueIdForSeasonAsync(client, saveId, 2);
            season2League.ShouldNotBe(season1League);
            await AssertHistoricalPinnedAsync(client, saveId, season1League, season2League);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> GetAthleteJsonAsync(HttpClient client, Guid saveId, int athleteId)
    {
        using HttpResponseMessage jsonFirst = await client.GetAsync($"/api/saves/{saveId:D}/athletes/{athleteId}/report?format=json").ConfigureAwait(false);
        jsonFirst.StatusCode.ShouldBe(HttpStatusCode.OK);
        jsonFirst.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        jsonFirst.Content.Headers.ContentDisposition?.DispositionType.ShouldBe("attachment");
        string file = jsonFirst.Content.Headers.ContentDisposition?.FileNameStar ?? jsonFirst.Content.Headers.ContentDisposition?.FileName ?? string.Empty;
        file.ShouldContain($"athlete-{athleteId}-");
        file.ShouldEndWith(".json");
        string body = await jsonFirst.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("reportType").GetString().ShouldBe("athlete-profile");
        json.RootElement.GetProperty("scope").GetProperty("athleteId").GetInt32().ShouldBe(athleteId);
        return body;
    }

    private static async Task<string> GetAthleteMarkdownAsync(HttpClient client, Guid saveId, int athleteId, string jsonBody)
    {
        using JsonDocument json = JsonDocument.Parse(jsonBody);
        string name = json.RootElement.GetProperty("athlete").GetProperty("name").GetString()!;
        int careerWins = json.RootElement.GetProperty("career").GetProperty("roundWins").GetInt32();
        using HttpResponseMessage md = await client.GetAsync($"/api/saves/{saveId:D}/athletes/{athleteId}/report?format=md").ConfigureAwait(false);
        md.StatusCode.ShouldBe(HttpStatusCode.OK);
        md.Content.Headers.ContentType?.MediaType.ShouldBe("text/markdown");
        string mdBody = await md.Content.ReadAsStringAsync().ConfigureAwait(false);
        mdBody.ShouldContain("# Athlete report");
        mdBody.ShouldContain(name);
        mdBody.ShouldContain("## Seasons");
        mdBody.ShouldContain("## Honours");
        mdBody.ShouldContain("## Cup history");
        mdBody.ShouldContain(careerWins.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return mdBody;
    }

    private static async Task AssertAthleteDeterministicAsync(HttpClient client, Guid saveId, int athleteId, string jsonBody)
    {
        using HttpResponseMessage jsonSecond = await client.GetAsync($"/api/saves/{saveId:D}/athletes/{athleteId}/report?format=json").ConfigureAwait(false);
        string body2 = await jsonSecond.Content.ReadAsStringAsync().ConfigureAwait(false);
        Normalize(jsonBody).ShouldBe(Normalize(body2));
    }

    private static async Task AssertAthleteBadIdentifiersAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage missing = await client.GetAsync($"/api/saves/{saveId:D}/athletes/999999/report?format=json").ConfigureAwait(false);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using HttpResponseMessage badFormat = await client.GetAsync($"/api/saves/{saveId:D}/athletes/1/report?format=pdf").ConfigureAwait(false);
        badFormat.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using HttpResponseMessage badAthlete = await client.GetAsync($"/api/saves/{saveId:D}/athletes/0/report?format=json").ConfigureAwait(false);
        badAthlete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using HttpResponseMessage badSave = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/athletes/1/report?format=json").ConfigureAwait(false);
        badSave.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task AssertProvisionalAsync(HttpClient client, Guid saveId, int leagueId)
    {
        using HttpResponseMessage provisional = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{leagueId}/seasons/1/report?format=json").ConfigureAwait(false);
        provisional.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument provJson = JsonDocument.Parse(await provisional.Content.ReadAsStringAsync().ConfigureAwait(false));
        provJson.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        provJson.RootElement.GetProperty("reportType").GetString().ShouldBe("standings-table");
        provJson.RootElement.GetProperty("snapshot").GetProperty("status").GetString().ShouldBe("provisional");
        provJson.RootElement.GetProperty("snapshot").GetProperty("completedStages").GetInt32().ShouldBe(1);
        provJson.RootElement.GetProperty("standings").GetArrayLength().ShouldBe(32);
        foreach (JsonElement row in provJson.RootElement.GetProperty("standings").EnumerateArray())
        {
            row.GetProperty("zone").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    private static async Task AssertFinalAsync(HttpClient client, Guid saveId, int leagueId)
    {
        using HttpResponseMessage final = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{leagueId}/seasons/1/report?format=json").ConfigureAwait(false);
        final.StatusCode.ShouldBe(HttpStatusCode.OK);
        string finalBody = await final.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument finalJson = JsonDocument.Parse(finalBody);
        finalJson.RootElement.GetProperty("snapshot").GetProperty("status").GetString().ShouldBe("final");
        finalJson.RootElement.GetProperty("snapshot").GetProperty("completedStages").GetInt32().ShouldBe(32);
        var ranks = finalJson.RootElement.GetProperty("standings").EnumerateArray().Select(r => r.GetProperty("rank").GetInt32()).ToList();
        ranks.ShouldBe(Enumerable.Range(1, 32).ToList());
        JsonElement champion = finalJson.RootElement.GetProperty("standings").EnumerateArray().First(r => r.GetProperty("rank").GetInt32() == 1);
        champion.GetProperty("isChampion").GetBoolean().ShouldBeTrue();
        await AssertFinalMarkdownAsync(client, saveId, leagueId, champion).ConfigureAwait(false);
    }

    private static async Task AssertFinalMarkdownAsync(HttpClient client, Guid saveId, int leagueId, JsonElement champion)
    {
        using HttpResponseMessage finalMd = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{leagueId}/seasons/1/report?format=md").ConfigureAwait(false);
        finalMd.StatusCode.ShouldBe(HttpStatusCode.OK);
        string mdBody = await finalMd.Content.ReadAsStringAsync().ConfigureAwait(false);
        mdBody.ShouldContain("# Standings report");
        mdBody.ShouldContain("final table");
        mdBody.ShouldContain(champion.GetProperty("name").GetString()!);
    }

    private static async Task AssertNoFallbackAsync(HttpClient client, Guid saveId, int leagueId)
    {
        using HttpResponseMessage noSeason = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{leagueId}/seasons/9/report?format=json").ConfigureAwait(false);
        noSeason.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using HttpResponseMessage noLeague = await client.GetAsync($"/api/saves/{saveId:D}/leagues/999999/seasons/1/report?format=json").ConfigureAwait(false);
        noLeague.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using HttpResponseMessage badFormat = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{leagueId}/seasons/1/report?format=csv").ConfigureAwait(false);
        badFormat.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task AssertHistoricalPinnedAsync(HttpClient client, Guid saveId, int season1League, int season2League)
    {
        using HttpResponseMessage historical = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{season1League}/seasons/1/report?format=json").ConfigureAwait(false);
        historical.StatusCode.ShouldBe(HttpStatusCode.OK);
        using JsonDocument histJson = JsonDocument.Parse(await historical.Content.ReadAsStringAsync().ConfigureAwait(false));
        histJson.RootElement.GetProperty("snapshot").GetProperty("status").GetString().ShouldBe("final");
        using HttpResponseMessage current = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{season2League}/seasons/2/report?format=json").ConfigureAwait(false);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage cross = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{season1League}/seasons/2/report?format=json").ConfigureAwait(false);
        cross.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using HttpResponseMessage crossBack = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{season2League}/seasons/1/report?format=json").ConfigureAwait(false);
        crossBack.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task AssertReadOnlyAsync(string root, Guid saveId, long rngBefore, long streamBefore, int standingsBefore)
    {
        (long rngAfter, long streamAfter, int standingsAfter) = await ReadRngAndStandingsAsync(root, saveId).ConfigureAwait(false);
        rngAfter.ShouldBe(rngBefore);
        streamAfter.ShouldBe(streamBefore);
        standingsAfter.ShouldBe(standingsBefore);
    }

    private static async Task CompleteOneStageAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null).ConfigureAwait(false);
        bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task CompleteSeasonAsync(HttpClient client, Guid saveId)
    {
        for (int i = 0; i < 31; i++)
        {
            using HttpResponseMessage next = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null).ConfigureAwait(false);
            next.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
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

    private static async Task<(long Rng, long Stream, int Standings)> ReadRngAndStandingsAsync(string root, Guid saveId)
    {
        string path = Path.Combine(root, "saves", $"{saveId:N}.db");
        var options = new DbContextOptionsBuilder<SaveDbContext>().UseSqlite($"Data Source={path}").Options;
        using var context = new SaveDbContext(options);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        int standings = await context.SeasonStandings.AsNoTracking().CountAsync().ConfigureAwait(false);
        return (rng.State, rng.Stream, standings);
    }

    private static async Task<int> FirstLeagueIdAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage progress = await client.GetAsync($"/api/saves/{saveId:D}/seasons/1/progress").ConfigureAwait(false);
        progress.StatusCode.ShouldBe(HttpStatusCode.OK);
        ProgressPayload? payload = await progress.Content.ReadFromJsonAsync<ProgressPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Leagues.OrderBy(l => l.LeagueId).First().LeagueId;
    }

    private static async Task<int> FirstLeagueIdForSeasonAsync(HttpClient client, Guid saveId, int seasonNumber)
    {
        using HttpResponseMessage competitions = await client.GetAsync($"/api/saves/{saveId:D}/history/seasons/{seasonNumber}/competitions").ConfigureAwait(false);
        competitions.StatusCode.ShouldBe(HttpStatusCode.OK);
        CompetitionsPayload? payload = await competitions.Content.ReadFromJsonAsync<CompetitionsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Competitions.OrderBy(l => l.LeagueId).First().LeagueId;
    }

    private static async Task<int> FirstStandingAthleteAsync(HttpClient client, Guid saveId, int leagueId)
    {
        using HttpResponseMessage standings = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{leagueId}/standings/current").ConfigureAwait(false);
        standings.StatusCode.ShouldBe(HttpStatusCode.OK);
        StandingsPayload? payload = await standings.Content.ReadFromJsonAsync<StandingsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Standings[0].AthleteId;
    }

    private static async Task<Guid> CreateSaveAsync(HttpClient client, string name, ulong seed, ulong stream)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync("/api/saves", new { name, seed, stream }).ConfigureAwait(false);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        SavePayload? payload = await created.Content.ReadFromJsonAsync<SavePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.SaveId;
    }

    private static async Task SeedCatalogAsync(HttpClient client)
    {
        string bulkJson = UniverseTestCatalog.BuildBulkJson();
        using StringContent content = new(bulkJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage imported = await client.PostAsync("/api/catalog/import", content).ConfigureAwait(false);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-reports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }

    private sealed record SavePayload(Guid SaveId);

    private sealed record ProgressPayload(
        Guid SaveId,
        int SeasonNumber,
        int GlobalStage,
        bool IsSeasonComplete,
        IReadOnlyList<ProgressLeaguePayload> Leagues);

    private sealed record ProgressLeaguePayload(
        int LeagueId,
        string LeagueName,
        int? CurrentStage,
        int CompletedStages,
        bool IsLeagueComplete);

    private sealed record StandingsPayload(
        Guid SaveId,
        int SeasonNumber,
        int LeagueId,
        string LeagueName,
        int CompletedStages,
        int GlobalStage,
        bool IsSeasonComplete,
        bool IsFinal,
        string SeasonChecksum,
        IReadOnlyList<StandingsEntryPayload> Standings);

    private sealed record StandingsEntryPayload(
        int AthleteId,
        string Name,
        int SeasonRank,
        int TotalChampionshipPointsThousandths,
        int TotalStageScoreThousandths,
        int TotalBaseScoreThousandths,
        int StageWins,
        int RoundWins,
        bool IsChampion);

    private sealed record CompetitionsPayload(
        Guid SaveId,
        int SeasonNumber,
        bool IsSeasonComplete,
        IReadOnlyList<CompetitionPayload> Competitions);

    private sealed record CompetitionPayload(int LeagueId, string Name);
}
