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

namespace MtgSoloSports.Tests.Features.Simulation;

public sealed class AdvanceRoundApiTests
{
    [Fact]
    public async Task AdvanceRound_ViaApi_PersistsAndReturnsImmutableDto()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            Guid saveId = await CreateSaveAsync(client, "Round Api", 101UL, 202UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);

            using HttpResponseMessage first = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/rounds/advance", null);
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            AdvancePayload? firstPayload = await first.Content.ReadFromJsonAsync<AdvancePayload>();
            firstPayload.ShouldNotBeNull();
            AssertAdvancePayload(firstPayload, 1);

            using HttpResponseMessage second = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/rounds/advance", null);
            second.StatusCode.ShouldBe(HttpStatusCode.OK);
            AdvancePayload? secondPayload = await second.Content.ReadFromJsonAsync<AdvancePayload>();
            secondPayload.ShouldNotBeNull();
            AssertAdvancePayload(secondPayload, 2);
            secondPayload.RngBeforeState.ShouldBe(firstPayload.RngAfterState);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdvanceRound_UnknownSave_ReturnsNotFound_UnknownLeague_ReturnsConflict()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Round Api Errors", 55UL, 66UL);

            using HttpResponseMessage unknownSave = await client.PostAsync(
                $"/api/saves/{Guid.NewGuid():D}/leagues/1/rounds/advance", null);
            unknownSave.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage unknownLeague = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/999999/rounds/advance", null);
            unknownLeague.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AdvanceRound_SixteenRounds_ThenConflict()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Round Api Sixteen", 77UL, 88UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);

            for (int i = 1; i <= 16; i++)
            {
                using HttpResponseMessage response = await client.PostAsync(
                    $"/api/saves/{saveId:D}/leagues/{leagueId}/rounds/advance", null);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            using HttpResponseMessage conflict = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/rounds/advance", null);
            conflict.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertAdvancePayload(AdvancePayload payload, int expectedRound)
    {
        payload.StageNumber.ShouldBe(1);
        payload.RoundNumber.ShouldBe(expectedRound);
        payload.Placements.Count.ShouldBe(32);
        payload.PayloadChecksum.Length.ShouldBe(64);
        payload.Placements.Select(p => p.Position).ShouldBe(Enumerable.Range(1, 32).ToList());
    }

    private static async Task<Guid> CreateSaveAsync(HttpClient client, string name, ulong seed, ulong stream)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/saves",
            new { name, seed, stream }).ConfigureAwait(false);
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-round-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record LeaguePayload(
        string LeagueName,
        string SportingColor,
        int LeagueId,
        IReadOnlyList<object> Athletes);

    private sealed record AdvancePayload(
        Guid SaveId,
        int SeasonNumber,
        int LeagueId,
        string LeagueName,
        int StageNumber,
        int RoundNumber,
        int RulesVersion,
        string PayloadChecksum,
        ulong RngBeforeState,
        ulong RngBeforeStream,
        ulong RngAfterState,
        ulong RngAfterStream,
        IReadOnlyList<PlacementPayload> Placements);

    private sealed record PlacementPayload(
        int AthleteId,
        string Name,
        int Position,
        int BaseThousandths,
        int ActiveBonusThousandths,
        int FinalThousandths,
        int CumulativeBeforeThousandths,
        int CumulativeAfterThousandths,
        int RankBefore,
        int RankAfter,
        int RankMovement);
}
