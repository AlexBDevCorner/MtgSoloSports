using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Stories;

public sealed class StoryEventsApiTests
{
    [Fact]
    public async Task RecentAndAthleteStories_ReturnRenderedTextAfterStage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Story API", 4242UL, 777UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);

            await CompleteStageAsync(client, saveId, leagueId);

            StoryFeedPayload recent = await FetchRecentAsync(client, saveId);
            recent.Stories.Count.ShouldBeGreaterThanOrEqualTo(1);
            foreach (StoryItemPayload story in recent.Stories)
            {
                story.Text.Length.ShouldBeGreaterThan(0);
                story.ContextJson.Length.ShouldBeGreaterThan(0);
            }

            int athleteId = recent.Stories[0].AthleteId;
            StoryFeedPayload athlete = await FetchAthleteStoriesAsync(client, saveId, athleteId);
            athlete.Stories.Count.ShouldBeGreaterThanOrEqualTo(1);
            athlete.Stories.All(s => s.AthleteId == athleteId).ShouldBeTrue();

            await AssertNotFoundAsync(client, saveId, athleteId);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    internal static async Task AssertNotFoundAsync(HttpClient client, Guid saveId, int athleteId)
    {
        using HttpResponseMessage unknownSave = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/stories/recent").ConfigureAwait(false);
        unknownSave.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using HttpResponseMessage unknownAthlete = await client.GetAsync($"/api/saves/{saveId:D}/athletes/999999/stories").ConfigureAwait(false);
        unknownAthlete.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        _ = athleteId;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    internal static async Task<StoryFeedPayload> FetchRecentAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/stories/recent").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        StoryFeedPayload? payload = await response.Content.ReadFromJsonAsync<StoryFeedPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    internal static async Task<StoryFeedPayload> FetchAthleteStoriesAsync(HttpClient client, Guid saveId, int athleteId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/athletes/{athleteId}/stories").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        StoryFeedPayload? payload = await response.Content.ReadFromJsonAsync<StoryFeedPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    internal static async Task CompleteStageAsync(HttpClient client, Guid saveId, int leagueId)
    {
        using HttpResponseMessage response = await client.PostAsync(
            $"/api/saves/{saveId:D}/leagues/{leagueId}/stages/complete", content: null).ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    internal static async Task<int> FirstLeagueIdAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage leagues = await client.GetAsync($"/api/saves/{saveId:D}/seasons/1/leagues").ConfigureAwait(false);
        leagues.StatusCode.ShouldBe(HttpStatusCode.OK);
        LeaguesPayload? payload = await leagues.Content.ReadFromJsonAsync<LeaguesPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Leagues[0].LeagueId;
    }

    internal static async Task<Guid> CreateSaveAsync(HttpClient client, string name, ulong seed, ulong stream)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/saves",
            new { name, seed, stream }).ConfigureAwait(false);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        SavePayload? payload = await created.Content.ReadFromJsonAsync<SavePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.SaveId;
    }

    internal static async Task SeedCatalogAsync(HttpClient client)
    {
        string bulkJson = UniverseTestCatalog.BuildBulkJson();
        using StringContent content = new(bulkJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage imported = await client.PostAsync("/api/catalog/import", content).ConfigureAwait(false);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    internal static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-stories-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }

    internal sealed record SavePayload(Guid SaveId);

    internal sealed record LeaguesPayload(
        Guid SaveId,
        int SeasonNumber,
        bool HasSuperleague,
        string DrawChecksum,
        int ActiveAthletes,
        int PoolAthletes,
        IReadOnlyList<LeaguePayload> Leagues,
        IReadOnlyList<object> PoolCounts);

    internal sealed record LeaguePayload(
        string LeagueName,
        string SportingColor,
        int LeagueId,
        IReadOnlyList<object> Athletes);

    internal sealed record StoryFeedPayload(
        Guid SaveId,
        IReadOnlyList<StoryItemPayload> Stories);

    internal sealed record StoryItemPayload(
        int Id,
        string EventType,
        int AthleteId,
        string AthleteName,
        int SeasonNumber,
        int? StageNumber,
        string ContextJson,
        string Text);
}
