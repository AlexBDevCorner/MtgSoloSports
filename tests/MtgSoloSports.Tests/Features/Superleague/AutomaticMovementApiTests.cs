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

namespace MtgSoloSports.Tests.Features.Superleague;

public sealed class AutomaticMovementApiTests
{
    [Fact]
    public async Task AutomaticMovement_BeforeResolved_PostConflicts_GetNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Auto Gates", 11UL, 22UL);

            using HttpResponseMessage post = await client.PostAsync($"/api/saves/{saveId:D}/superleague/automatic-movement", null);
            post.StatusCode.ShouldBe(HttpStatusCode.Conflict);

            using HttpResponseMessage get = await client.GetAsync($"/api/saves/{saveId:D}/superleague/automatic-movement");
            get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AutomaticMovement_UnknownSave_ReturnsNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage post = await client.PostAsync($"/api/saves/{Guid.NewGuid():D}/superleague/automatic-movement", null);
            post.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage get = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/superleague/automatic-movement");
            get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
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

    private static async Task SeedCatalogAsync(HttpClient client)
    {
        string bulkJson = UniverseTestCatalog.BuildBulkJson();
        using StringContent content = new(bulkJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage imported = await client.PostAsync("/api/catalog/import", content).ConfigureAwait(false);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-auto-api-" + Guid.NewGuid().ToString("N"));
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
}
