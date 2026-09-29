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

namespace MtgSoloSports.Tests.Features.History;

public sealed class SeasonPlacementsApiTests
{
    [Fact]
    public async Task PlacementsEndpoint_ReturnsCompactMatrix_AndRejectsBadIds()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Placements API", 1701UL, 1702UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);

            // Empty season: roster present, no cells.
            PlacementsPayload empty = await FetchPlacementsAsync(client, saveId, 1, leagueId);
            empty.Athletes.Count.ShouldBe(32);
            empty.Placements.ShouldBeEmpty();
            empty.CompletedStages.ShouldBe(0);

            // Complete one stage for the target league only.
            await CompleteStageAsync(client, saveId, leagueId);
            PlacementsPayload one = await FetchPlacementsAsync(client, saveId, 1, leagueId);
            one.CompletedStages.ShouldBe(1);
            one.Placements.Count.ShouldBe(32);

            // Endpoint error mapping stays JSON 404/400, never a white screen.
            using HttpResponseMessage unknownSave = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/history/seasons/1/competitions/{leagueId}/placements");
            unknownSave.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage unknownSeason = await client.GetAsync($"/api/saves/{saveId:D}/history/seasons/99/competitions/{leagueId}/placements");
            unknownSeason.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage unknownLeague = await client.GetAsync($"/api/saves/{saveId:D}/history/seasons/1/competitions/999999/placements");
            unknownLeague.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage badSeason = await client.GetAsync($"/api/saves/{saveId:D}/history/seasons/0/competitions/{leagueId}/placements");
            badSeason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<PlacementsPayload> FetchPlacementsAsync(HttpClient client, Guid saveId, int season, int leagueId)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/saves/{saveId:D}/history/seasons/{season}/competitions/{leagueId}/placements").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        PlacementsPayload? payload = await response.Content.ReadFromJsonAsync<PlacementsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task CompleteStageAsync(HttpClient client, Guid saveId, int leagueId)
    {
        using HttpResponseMessage completed = await client.PostAsync(
            $"/api/saves/{saveId:D}/leagues/{leagueId}/stages/complete", null).ConfigureAwait(false);
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<Guid> CreateSaveAsync(HttpClient client, string name, ulong seed, ulong stream)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync("/api/saves", new { name, seed, stream }).ConfigureAwait(false);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        SavePayload? payload = await created.Content.ReadFromJsonAsync<SavePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.SaveId;
    }

    private static async Task<int> FirstLeagueIdAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage leagues = await client.GetAsync($"/api/saves/{saveId:D}/seasons/1/leagues").ConfigureAwait(false);
        leagues.StatusCode.ShouldBe(HttpStatusCode.OK);
        LeaguesPayload? payload = await leagues.Content.ReadFromJsonAsync<LeaguesPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Leagues[0].LeagueId;
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-placements-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record LeaguesPayload(
        Guid SaveId,
        int SeasonNumber,
        bool HasSuperleague,
        string DrawChecksum,
        int ActiveAthletes,
        int PoolAthletes,
        IReadOnlyList<LeaguePayload> Leagues,
        IReadOnlyList<object> PoolCounts);

    private sealed record LeaguePayload(string LeagueName, string SportingColor, int LeagueId, IReadOnlyList<object> Athletes);

    private sealed record PlacementsPayload(
        Guid SaveId,
        int SeasonNumber,
        int LeagueId,
        string LeagueName,
        string LeagueKind,
        bool IsSeasonComplete,
        int CompletedStages,
        IReadOnlyList<object> Athletes,
        IReadOnlyList<PlacementCell> Placements);

    private sealed record PlacementCell(
        int AthleteId,
        int StageNumber,
        int StageRank,
        int EarnedBonusThousandths,
        int ChampionshipPointsThousandths,
        int StageScoreThousandths,
        int RoundWins);
}
