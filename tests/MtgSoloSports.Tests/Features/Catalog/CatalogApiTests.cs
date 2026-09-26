using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Catalog;

public sealed class CatalogApiTests
{
    [Fact]
    public async Task ImportThenStats_ViaApi()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            string bulkJson = """
                [
                  {"name": "White One", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
                ]
                """;

            using StringContent content = new(bulkJson, Encoding.UTF8, "application/json");
            using HttpResponseMessage imported = await client.PostAsync("/api/catalog/import", content);
            imported.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonElement payload = await imported.Content.ReadFromJsonAsync<JsonElement>();
            payload.GetProperty("uniqueAthletes").GetInt32().ShouldBe(1);

            using HttpResponseMessage stats = await client.GetAsync("/api/catalog/stats");
            stats.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonElement statsPayload = await stats.Content.ReadFromJsonAsync<JsonElement>();
            statsPayload.GetProperty("totalAthletes").GetInt32().ShouldBe(1);
            statsPayload.GetProperty("isSufficientForSave").GetBoolean().ShouldBeFalse();
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportEmptyBody_ReturnsBadRequest()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            using StringContent content = new(string.Empty, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync("/api/catalog/import", content);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-catalog-api-" + Guid.NewGuid().ToString("N"));
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
