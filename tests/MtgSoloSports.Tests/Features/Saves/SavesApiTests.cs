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

namespace MtgSoloSports.Tests.Features.Saves;

public sealed class SavesApiTests
{
    [Fact]
    public async Task FullSaveLifecycle_ViaApi()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage created = await client.PostAsJsonAsync(
                "/api/saves",
                new { name = "Api Universe", seed = 101UL, stream = 202UL });
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            SavePayload? createdPayload = await created.Content.ReadFromJsonAsync<SavePayload>();
            createdPayload.ShouldNotBeNull();
            createdPayload.Name.ShouldBe("Api Universe");
            createdPayload.SchemaVersion.ShouldBe(SaveSchemaVersion.Current);
            createdPayload.CurrentSeason.ShouldBe(1);
            createdPayload.Phase.ShouldBe("SeasonInProgress");
            createdPayload.RulesVersion.ShouldBe(1);
            createdPayload.TotalAthletes.ShouldBe(2048);
            createdPayload.AthletesPerColor.Count.ShouldBe(8);
            foreach (int count in createdPayload.AthletesPerColor.Values)
            {
                count.ShouldBe(256);
            }

            createdPayload.UniverseChecksum.Length.ShouldBe(64);
            created.Headers.Location.ShouldNotBeNull();

            using HttpResponseMessage opened = await client.GetAsync($"/api/saves/{createdPayload.SaveId:D}");
            opened.StatusCode.ShouldBe(HttpStatusCode.OK);
            SavePayload? openedPayload = await opened.Content.ReadFromJsonAsync<SavePayload>();
            openedPayload.ShouldNotBeNull();
            openedPayload.SaveId.ShouldBe(createdPayload.SaveId);
            openedPayload.RngState.ShouldBe(createdPayload.RngState);

            using HttpResponseMessage listed = await client.GetAsync("/api/saves");
            listed.StatusCode.ShouldBe(HttpStatusCode.OK);
            ListPayload? listPayload = await listed.Content.ReadFromJsonAsync<ListPayload>();
            listPayload.ShouldNotBeNull();
            listPayload.Saves.Count.ShouldBe(1);
            listPayload.Saves[0].SaveId.ShouldBe(createdPayload.SaveId);

            using HttpResponseMessage deleted = await client.DeleteAsync($"/api/saves/{createdPayload.SaveId:D}");
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            using HttpResponseMessage reopened = await client.GetAsync($"/api/saves/{createdPayload.SaveId:D}");
            reopened.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            Directory.GetFiles(Path.Combine(root, "saves"), "*.db").Length.ShouldBe(0);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateSave_WithoutCatalog_ReturnsBadRequest()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/saves",
                new { name = "No Catalog", seed = 1UL, stream = 2UL });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            string body = await response.Content.ReadAsStringAsync();
            body.ShouldContain("256");
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateSave_InvalidName_ReturnsBadRequest()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/saves",
                new { name = "   " });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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
            Guid missing = Guid.NewGuid();
            using HttpResponseMessage opened = await client.GetAsync($"/api/saves/{missing:D}");
            opened.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            using HttpResponseMessage deleted = await client.DeleteAsync($"/api/saves/{missing:D}");
            deleted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoApiSaves_UseSeparateFiles()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage first = await client.PostAsJsonAsync("/api/saves", new { name = "One" });
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
            using HttpResponseMessage second = await client.PostAsJsonAsync("/api/saves", new { name = "Two" });
            second.StatusCode.ShouldBe(HttpStatusCode.Created);

            Directory.GetFiles(Path.Combine(root, "saves"), "*.db").Length.ShouldBe(2);

            using HttpResponseMessage listed = await client.GetAsync("/api/saves");
            ListPayload? listPayload = await listed.Content.ReadFromJsonAsync<ListPayload>();
            listPayload.ShouldNotBeNull();
            listPayload.Saves.Count.ShouldBe(2);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }

    private sealed record SavePayload(
        Guid SaveId,
        string Name,
        DateTimeOffset CreatedUtc,
        int SchemaVersion,
        int CurrentSeason,
        string Phase,
        string RngAlgorithm,
        int RngVersion,
        string RngState,
        string RngStream,
        int RulesVersion,
        int TotalAthletes,
        Dictionary<string, int> AthletesPerColor,
        string UniverseChecksum);

    private sealed record ListPayload(IReadOnlyList<SavePayload> Saves);
}
