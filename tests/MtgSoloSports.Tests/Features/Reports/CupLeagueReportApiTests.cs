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
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

public sealed class CupLeagueReportApiTests
{
    [Fact]
    public async Task CupEdition_Color_JsonAndMarkdownAgreeAndAreDeterministic()
    {
        var (factory, root) = CreateFactory();
        string savesRoot = Path.Combine(root, "saves");
        Directory.CreateDirectory(savesRoot);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(savesRoot);
            using HttpClient client = factory.CreateClient();
            await AssertColorEditionAsync(client, root, saveId);
            await AssertColorEditionNoFallbackAsync(client, saveId);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CupTeam_Color_HistoryRosterAndResults()
    {
        var (factory, root) = CreateFactory();
        string savesRoot = Path.Combine(root, "saves");
        Directory.CreateDirectory(savesRoot);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(savesRoot);
            using HttpClient client = factory.CreateClient();
            await AssertColorTeamAsync(client, saveId);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LeagueProfile_CompletePodiumLeaderboardAndRecords()
    {
        var (factory, root) = CreateFactory();
        string savesRoot = Path.Combine(root, "saves");
        Directory.CreateDirectory(savesRoot);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(savesRoot);
            using HttpClient client = factory.CreateClient();
            await AssertLeagueProfileAsync(client, saveId);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TypeCup_Edition_DistinguishesQualificationAndFinal()
    {
        var (factory, root) = CreateFactory();
        string savesRoot = Path.Combine(root, "saves");
        Directory.CreateDirectory(savesRoot);
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(savesRoot);
            using HttpClient client = factory.CreateClient();
            await SimulateOneSeasonAsync(client, saveId);
            await AssertTypeEditionAsync(client, saveId);
            await AssertTypeTeamAsync(client, saveId);
        }
        finally
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertColorEditionAsync(HttpClient client, string root, Guid saveId)
    {
        (long rngBefore, long streamBefore, int standingsBefore) = await ReadRngAndStandingsAsync(root, saveId).ConfigureAwait(false);
        string jsonBody = await GetColorEditionJsonAsync(client, saveId).ConfigureAwait(false);
        await AssertColorMarkdownAsync(client, saveId, jsonBody).ConfigureAwait(false);
        await AssertColorDeterministicAsync(client, saveId, jsonBody).ConfigureAwait(false);
        (long rngAfter, long streamAfter, int standingsAfter) = await ReadRngAndStandingsAsync(root, saveId).ConfigureAwait(false);
        rngAfter.ShouldBe(rngBefore);
        streamAfter.ShouldBe(streamBefore);
        standingsAfter.ShouldBe(standingsBefore);
    }

    private static async Task<string> GetColorEditionJsonAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage jsonFirst = await client.GetAsync($"/api/saves/{saveId:D}/cups/color/editions/1/report?format=json").ConfigureAwait(false);
        jsonFirst.StatusCode.ShouldBe(HttpStatusCode.OK);
        jsonFirst.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        string file = jsonFirst.Content.Headers.ContentDisposition?.FileNameStar ?? jsonFirst.Content.Headers.ContentDisposition?.FileName ?? string.Empty;
        file.ShouldContain("cup-color-season-1-edition");
        file.ShouldEndWith(".json");
        string jsonBody = await jsonFirst.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(jsonBody);
        json.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("reportType").GetString().ShouldBe("cup-edition");
        json.RootElement.GetProperty("scope").GetProperty("cup").GetString().ShouldBe("Color");
        json.RootElement.GetProperty("scope").GetProperty("sourceSeasonNumber").GetInt32().ShouldBe(1);
        json.RootElement.GetProperty("snapshot").GetProperty("state").GetString().ShouldBe("Completed");
        JsonElement squads = json.RootElement.GetProperty("selection").GetProperty("teams");
        squads.GetArrayLength().ShouldBe(8);
        foreach (JsonElement team in squads.EnumerateArray())
        {
            team.GetProperty("members").GetArrayLength().ShouldBe(4);
        }

        json.RootElement.TryGetProperty("colorIndividual", out JsonElement individual).ShouldBeTrue();
        individual.ValueKind.ShouldNotBe(JsonValueKind.Null);
        individual.GetProperty("standings").GetArrayLength().ShouldBe(32);
        json.RootElement.TryGetProperty("colorTeam", out JsonElement teamResult).ShouldBeTrue();
        teamResult.ValueKind.ShouldNotBe(JsonValueKind.Null);
        teamResult.GetProperty("teams").GetArrayLength().ShouldBe(8);
        teamResult.GetProperty("legs").GetArrayLength().ShouldBe(32);
        return jsonBody;
    }

    private static async Task AssertColorMarkdownAsync(HttpClient client, Guid saveId, string jsonBody)
    {
        using JsonDocument json = JsonDocument.Parse(jsonBody);
        string champion = json.RootElement.GetProperty("colorIndividual").GetProperty("championName").GetString()!;
        using HttpResponseMessage md = await client.GetAsync($"/api/saves/{saveId:D}/cups/color/editions/1/report?format=md").ConfigureAwait(false);
        md.StatusCode.ShouldBe(HttpStatusCode.OK);
        md.Content.Headers.ContentType?.MediaType.ShouldBe("text/markdown");
        string mdBody = await md.Content.ReadAsStringAsync().ConfigureAwait(false);
        mdBody.ShouldContain("# Color Cup edition report");
        mdBody.ShouldContain("Season 1");
        mdBody.ShouldContain("## Squads");
        mdBody.ShouldContain("## Individual event");
        mdBody.ShouldContain("## Team event");
        mdBody.ShouldContain(champion);
    }

    private static async Task AssertColorDeterministicAsync(HttpClient client, Guid saveId, string jsonBody)
    {
        using HttpResponseMessage jsonSecond = await client.GetAsync($"/api/saves/{saveId:D}/cups/color/editions/1/report?format=json").ConfigureAwait(false);
        string body2 = await jsonSecond.Content.ReadAsStringAsync().ConfigureAwait(false);
        Normalize(jsonBody).ShouldBe(Normalize(body2));
    }

    private static async Task AssertColorEditionNoFallbackAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage wrongEdition = await client.GetAsync($"/api/saves/{saveId:D}/cups/color/editions/2/report?format=json").ConfigureAwait(false);
        wrongEdition.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using HttpResponseMessage wrongCup = await client.GetAsync($"/api/saves/{saveId:D}/cups/type/editions/1/report?format=json").ConfigureAwait(false);
        wrongCup.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using HttpResponseMessage badFormat = await client.GetAsync($"/api/saves/{saveId:D}/cups/color/editions/1/report?format=csv").ConfigureAwait(false);
        badFormat.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using HttpResponseMessage badCup = await client.GetAsync($"/api/saves/{saveId:D}/cups/gold/editions/1/report?format=json").ConfigureAwait(false);
        badCup.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task AssertColorTeamAsync(HttpClient client, Guid saveId)
    {
        string teamKey = await FirstColorTeamKeyAsync(client, saveId).ConfigureAwait(false);
        string memberName = await AssertColorTeamJsonAsync(client, saveId, teamKey).ConfigureAwait(false);
        await AssertColorTeamMarkdownAsync(client, saveId, teamKey, memberName).ConfigureAwait(false);
        using HttpResponseMessage missing = await client.GetAsync($"/api/saves/{saveId:D}/cups/color/teams/no-such-team/report?format=json").ConfigureAwait(false);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<string> AssertColorTeamJsonAsync(HttpClient client, Guid saveId, string teamKey)
    {
        using HttpResponseMessage json = await client.GetAsync($"/api/saves/{saveId:D}/cups/color/teams/{Uri.EscapeDataString(teamKey)}/report?format=json").ConfigureAwait(false);
        json.StatusCode.ShouldBe(HttpStatusCode.OK);
        string jsonBody = await json.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(jsonBody);
        doc.RootElement.GetProperty("reportType").GetString().ShouldBe("cup-team");
        doc.RootElement.GetProperty("scope").GetProperty("teamKey").GetString().ShouldBe(teamKey);
        doc.RootElement.GetProperty("honours").GetProperty("editions").GetInt32().ShouldBeGreaterThan(0);
        JsonElement seasons = doc.RootElement.GetProperty("seasons");
        seasons.GetArrayLength().ShouldBeGreaterThan(0);
        JsonElement firstSeason = seasons.EnumerateArray().First();
        firstSeason.GetProperty("squad").GetArrayLength().ShouldBe(4);
        firstSeason.GetProperty("sourceSeasonNumber").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("roster").GetArrayLength().ShouldBeGreaterThan(0);
        return firstSeason.GetProperty("squad").EnumerateArray().First().GetProperty("name").GetString()!;
    }

    private static async Task AssertColorTeamMarkdownAsync(HttpClient client, Guid saveId, string teamKey, string memberName)
    {
        using HttpResponseMessage md = await client.GetAsync($"/api/saves/{saveId:D}/cups/color/teams/{Uri.EscapeDataString(teamKey)}/report?format=md").ConfigureAwait(false);
        md.StatusCode.ShouldBe(HttpStatusCode.OK);
        string mdBody = await md.Content.ReadAsStringAsync().ConfigureAwait(false);
        mdBody.ShouldContain("# Color Cup team report");
        mdBody.ShouldContain("## Seasons");
        mdBody.ShouldContain("## All-time roster");
        mdBody.ShouldContain(memberName);
    }

    private static async Task AssertLeagueProfileAsync(HttpClient client, Guid saveId)
    {
        string leagueKey = await FirstLeagueKeyAsync(client, saveId).ConfigureAwait(false);
        string champion = await AssertLeagueJsonAsync(client, saveId, leagueKey).ConfigureAwait(false);
        await AssertLeagueMarkdownAsync(client, saveId, leagueKey, champion).ConfigureAwait(false);
        await AssertSuperleagueProfileAsync(client, saveId, leagueKey).ConfigureAwait(false);
        using HttpResponseMessage unknown = await client.GetAsync($"/api/saves/{saveId:D}/leagues/no-such-league/report?format=json").ConfigureAwait(false);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task AssertSuperleagueProfileAsync(HttpClient client, Guid saveId, string testedKey)
    {
        string? superleague = await FindSuperleagueKeyAsync(client, saveId).ConfigureAwait(false);
        if (superleague is null || string.Equals(superleague, testedKey, StringComparison.Ordinal))
        {
            return;
        }

        await AssertProvisionalLeagueAsync(client, saveId, superleague).ConfigureAwait(false);
    }

    private static async Task AssertProvisionalLeagueAsync(HttpClient client, Guid saveId, string leagueKey)
    {
        using HttpResponseMessage json = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{Uri.EscapeDataString(leagueKey)}/report?format=json").ConfigureAwait(false);
        json.StatusCode.ShouldBe(HttpStatusCode.OK);
        string jsonBody = await json.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(jsonBody);
        doc.RootElement.GetProperty("reportType").GetString().ShouldBe("league-profile");
        doc.RootElement.GetProperty("scope").GetProperty("leagueKey").GetString().ShouldBe(leagueKey);
        doc.RootElement.TryGetProperty("currentSeason", out JsonElement current).ShouldBeTrue();
        current.ValueKind.ShouldNotBe(JsonValueKind.Null);

        using HttpResponseMessage md = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{Uri.EscapeDataString(leagueKey)}/report?format=md").ConfigureAwait(false);
        md.StatusCode.ShouldBe(HttpStatusCode.OK);
        string mdBody = await md.Content.ReadAsStringAsync().ConfigureAwait(false);
        mdBody.ShouldContain("# League profile report");
        mdBody.ShouldContain("not yet final");
    }

    private static async Task<string> AssertLeagueJsonAsync(HttpClient client, Guid saveId, string leagueKey)
    {
        using HttpResponseMessage json = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{Uri.EscapeDataString(leagueKey)}/report?format=json").ConfigureAwait(false);
        json.StatusCode.ShouldBe(HttpStatusCode.OK);
        json.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        string jsonBody = await json.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(jsonBody);
        doc.RootElement.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("reportType").GetString().ShouldBe("league-profile");
        doc.RootElement.GetProperty("scope").GetProperty("leagueKey").GetString().ShouldBe(leagueKey);
        JsonElement podium = doc.RootElement.GetProperty("podiumHistory");
        podium.GetArrayLength().ShouldBeGreaterThan(0);
        JsonElement board = doc.RootElement.GetProperty("allTimeLeaderboard");
        board.GetArrayLength().ShouldBe(32);
        foreach (JsonElement row in board.EnumerateArray())
        {
            row.TryGetProperty("totalLeaguePointsThousandths", out _).ShouldBeTrue();
        }

        doc.RootElement.TryGetProperty("currentSeason", out JsonElement current).ShouldBeTrue();
        current.ValueKind.ShouldNotBe(JsonValueKind.Null);
        return podium.EnumerateArray().First().GetProperty("champion").GetProperty("athleteName").GetString()!;
    }

    private static async Task AssertLeagueMarkdownAsync(HttpClient client, Guid saveId, string leagueKey, string champion)
    {
        using HttpResponseMessage md = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{Uri.EscapeDataString(leagueKey)}/report?format=md").ConfigureAwait(false);
        md.StatusCode.ShouldBe(HttpStatusCode.OK);
        string mdBody = await md.Content.ReadAsStringAsync().ConfigureAwait(false);
        mdBody.ShouldContain("# League profile report");
        mdBody.ShouldContain("## Podium history");
        mdBody.ShouldContain("## All-time Total league points");
        mdBody.ShouldContain("## League records");
        mdBody.ShouldContain("not yet final");
        mdBody.ShouldContain(champion);
    }

    private static async Task SimulateOneSeasonAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage simulated = await client.PostAsJsonAsync($"/api/saves/{saveId:D}/simulate-seasons", new { seasons = 1 }).ConfigureAwait(false);
        simulated.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task AssertTypeEditionAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage json = await client.GetAsync($"/api/saves/{saveId:D}/cups/type/editions/2/report?format=json").ConfigureAwait(false);
        json.StatusCode.ShouldBe(HttpStatusCode.OK);
        string jsonBody = await json.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(jsonBody);
        doc.RootElement.GetProperty("reportType").GetString().ShouldBe("cup-edition");
        doc.RootElement.GetProperty("scope").GetProperty("cup").GetString().ShouldBe("Type");
        doc.RootElement.GetProperty("scope").GetProperty("sourceSeasonNumber").GetInt32().ShouldBe(2);
        doc.RootElement.GetProperty("snapshot").GetProperty("state").GetString().ShouldBe("Completed");
        doc.RootElement.TryGetProperty("typeTournament", out JsonElement tournament).ShouldBeTrue();
        tournament.ValueKind.ShouldNotBe(JsonValueKind.Null);
        int teamCount = tournament.GetProperty("teamCount").GetInt32();
        teamCount.ShouldBeGreaterThan(0);
        int finalistCount = tournament.GetProperty("finalists").GetArrayLength();
        finalistCount.ShouldBe(teamCount <= 32 ? teamCount : 32);
        tournament.TryGetProperty("final", out JsonElement final).ShouldBeTrue();
        final.ValueKind.ShouldNotBe(JsonValueKind.Null);
        final.GetProperty("teams").GetArrayLength().ShouldBe(finalistCount);
        string finalChampion = final.GetProperty("championCreatureType").GetString()!;
        finalChampion.Length.ShouldBeGreaterThan(0);
        AssertTournamentShape(tournament);

        using HttpResponseMessage md = await client.GetAsync($"/api/saves/{saveId:D}/cups/type/editions/2/report?format=md").ConfigureAwait(false);
        md.StatusCode.ShouldBe(HttpStatusCode.OK);
        string mdBody = await md.Content.ReadAsStringAsync().ConfigureAwait(false);
        mdBody.ShouldContain("# Type Cup edition report");
        mdBody.ShouldContain("## Tournament");
        mdBody.ShouldContain("official standings");
        mdBody.ShouldContain(finalChampion);
        if (!tournament.GetProperty("isDirectFinal").GetBoolean())
        {
            mdBody.ShouldContain("Qualification groups");
            mdBody.ShouldContain("not titles");
        }
        else
        {
            mdBody.ShouldContain("direct Final");
        }
    }

    private static void AssertTournamentShape(JsonElement tournament)
    {
        bool isDirect = tournament.GetProperty("isDirectFinal").GetBoolean();
        if (isDirect)
        {
            return;
        }

        tournament.GetProperty("qualificationGroups").GetArrayLength().ShouldBeGreaterThan(0);
        int policy = tournament.GetProperty("qualificationPolicyVersion").GetInt32();
        int wildcards = tournament.GetProperty("wildcardCount").GetInt32();
        if (policy == 2 && wildcards > 0)
        {
            tournament.TryGetProperty("wildcards", out JsonElement wildcardRows).ShouldBeTrue();
            wildcardRows.ValueKind.ShouldNotBe(JsonValueKind.Null);
            wildcardRows.GetArrayLength().ShouldBe(wildcards);
        }
    }

    private static async Task AssertTypeTeamAsync(HttpClient client, Guid saveId)
    {
        string teamKey = await FirstTypeTeamKeyAsync(client, saveId).ConfigureAwait(false);
        using HttpResponseMessage teamJson = await client.GetAsync($"/api/saves/{saveId:D}/cups/type/teams/{Uri.EscapeDataString(teamKey)}/report?format=json").ConfigureAwait(false);
        teamJson.StatusCode.ShouldBe(HttpStatusCode.OK);
        string teamBody = await teamJson.Content.ReadAsStringAsync().ConfigureAwait(false);
        using JsonDocument teamDoc = JsonDocument.Parse(teamBody);
        teamDoc.RootElement.GetProperty("reportType").GetString().ShouldBe("cup-team");
        teamDoc.RootElement.GetProperty("seasons").GetArrayLength().ShouldBeGreaterThan(0);
        using HttpResponseMessage teamMd = await client.GetAsync($"/api/saves/{saveId:D}/cups/type/teams/{Uri.EscapeDataString(teamKey)}/report?format=md").ConfigureAwait(false);
        teamMd.StatusCode.ShouldBe(HttpStatusCode.OK);
        string teamMdBody = await teamMd.Content.ReadAsStringAsync().ConfigureAwait(false);
        teamMdBody.ShouldContain("# Type Cup team report");
        teamMdBody.ShouldContain("## Seasons");
    }

    private static async Task<string> FirstColorTeamKeyAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage editions = await client.GetAsync($"/api/saves/{saveId:D}/cups/editions").ConfigureAwait(false);
        editions.StatusCode.ShouldBe(HttpStatusCode.OK);
        EditionsPayload? payload = await editions.Content.ReadFromJsonAsync<EditionsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        EditionsPayload.TeamSummary? first = payload.ColorTeams.OrderBy(t => t.TeamKey, StringComparer.Ordinal).FirstOrDefault();
        first.ShouldNotBeNull();
        return first.TeamKey;
    }

    private static async Task<string> FirstTypeTeamKeyAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage editions = await client.GetAsync($"/api/saves/{saveId:D}/cups/editions").ConfigureAwait(false);
        editions.StatusCode.ShouldBe(HttpStatusCode.OK);
        EditionsPayload? payload = await editions.Content.ReadFromJsonAsync<EditionsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        EditionsPayload.TeamSummary? first = payload.TypeTeams.OrderBy(t => t.TeamKey, StringComparer.Ordinal).FirstOrDefault();
        first.ShouldNotBeNull();
        return first.TeamKey;
    }

    private static async Task<string> FirstLeagueKeyAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage overview = await client.GetAsync($"/api/saves/{saveId:D}/leagues").ConfigureAwait(false);
        overview.StatusCode.ShouldBe(HttpStatusCode.OK);
        LeaguesOverviewPayload? payload = await overview.Content.ReadFromJsonAsync<LeaguesOverviewPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Leagues.OrderBy(l => l.LeagueKey, StringComparer.Ordinal).First().LeagueKey;
    }

    private static async Task<string?> FindSuperleagueKeyAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage overview = await client.GetAsync($"/api/saves/{saveId:D}/leagues").ConfigureAwait(false);
        overview.StatusCode.ShouldBe(HttpStatusCode.OK);
        LeaguesOverviewPayload? payload = await overview.Content.ReadFromJsonAsync<LeaguesOverviewPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Leagues.Select(l => l.LeagueKey).FirstOrDefault(k => string.Equals(k, "superleague", StringComparison.Ordinal));
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

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-cupreport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }

    private sealed record EditionsPayload(
        Guid SaveId,
        IReadOnlyList<object> Editions,
        IReadOnlyList<EditionsPayload.TeamSummary> ColorTeams,
        IReadOnlyList<EditionsPayload.TeamSummary> TypeTeams)
    {
        public sealed record TeamSummary(string TeamKey, string TeamName, int Editions, int Gold, int Silver, int Bronze, int? BestRank, int LastSeasonNumber);
    }

    private sealed record LeaguesOverviewPayload(
        Guid SaveId,
        IReadOnlyList<LeaguesOverviewPayload.LeagueSummary> Leagues)
    {
        public sealed record LeagueSummary(string LeagueKey, string Name);
    }
}
