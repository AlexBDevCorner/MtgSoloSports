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
            TestSaveStores.DeleteRoot(root);
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
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportAgainstDamagedCatalog_Returns500WithReImportGuidance()
    {
        var (factory, root) = CreateFactory();
        try
        {
            // A catalog file with a CatalogAthletes table the baseline path must reject:
            // server state damage is never a client error, and the message must be actionable.
            string catalogPath = Path.Combine(root, "catalog.db");
            using (Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={catalogPath}"))
            {
                await connection.OpenAsync();
                using System.Data.Common.DbCommand create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE "CatalogAthletes" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_CatalogAthletes" PRIMARY KEY,
                        "Name" TEXT NOT NULL
                    );
                    """;
                _ = await create.ExecuteNonQueryAsync();
            }

            using HttpClient client = factory.CreateClient();
            using StringContent content = new(
                """
                [
                  {"name": "White One", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
                ]
                """,
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage response = await client.PostAsync("/api/catalog/import", content);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            JsonElement payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            string? error = payload.GetProperty("error").GetString();
            error.ShouldNotBeNullOrEmpty();
            error.ShouldContain("The catalog is fully re-importable");
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
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
