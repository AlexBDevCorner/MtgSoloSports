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

namespace MtgSoloSports.Tests.Features.Leagues;

public sealed class Season1LeaguesApiTests
{
    [Fact]
    public async Task FullSeason1Flow_ViaApi()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage created = await client.PostAsJsonAsync(
                "/api/saves",
                new { name = "League Api", seed = 101UL, stream = 202UL });
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            SavePayload? createdPayload = await created.Content.ReadFromJsonAsync<SavePayload>();
            createdPayload.ShouldNotBeNull();

            using HttpResponseMessage leagues = await client.GetAsync($"/api/saves/{createdPayload.SaveId:D}/seasons/1/leagues");
            leagues.StatusCode.ShouldBe(HttpStatusCode.OK);
            LeaguesPayload? payload = await leagues.Content.ReadFromJsonAsync<LeaguesPayload>();
            payload.ShouldNotBeNull();
            AssertValidPayload(payload);

            using HttpResponseMessage replay = await client.GetAsync($"/api/saves/{createdPayload.SaveId:D}/seasons/1/leagues");
            LeaguesPayload? replayed = await replay.Content.ReadFromJsonAsync<LeaguesPayload>();
            replayed.ShouldNotBeNull();
            replayed.DrawChecksum.ShouldBe(payload.DrawChecksum);
            replayed.Leagues.Count.ShouldBe(24);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownSave_ReturnsNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/seasons/1/leagues");
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertValidPayload(LeaguesPayload payload)
    {
        payload.SeasonNumber.ShouldBe(1);
        payload.HasSuperleague.ShouldBeFalse();
        payload.ActiveAthletes.ShouldBe(768);
        payload.PoolAthletes.ShouldBe(1280);
        payload.DrawChecksum.Length.ShouldBe(64);
        payload.Leagues.Count.ShouldBe(24);
        payload.PoolCounts.Count.ShouldBe(8);
        foreach (LeaguePayload league in payload.Leagues)
        {
            league.Athletes.Count.ShouldBe(32);
        }

        foreach (PoolPayload pool in payload.PoolCounts)
        {
            pool.Count.ShouldBe(160);
        }

        List<string> names = payload.Leagues.SelectMany(l => l.Athletes).Select(a => a.Name).ToList();
        names.Count.ShouldBe(768);
        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(768);
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-season1-api-" + Guid.NewGuid().ToString("N"));
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
        IReadOnlyList<PoolPayload> PoolCounts);

    private sealed record LeaguePayload(
        string LeagueName,
        string SportingColor,
        int LeagueId,
        IReadOnlyList<RosterPayload> Athletes);

    private sealed record RosterPayload(string Name, int DrawIndex);

    private sealed record PoolPayload(string SportingColor, int Count);
}
