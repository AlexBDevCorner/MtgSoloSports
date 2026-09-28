using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Features.Catalog.ImportFromScryfall;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Catalog;

public sealed class ImportFromScryfallApiTests
{
    [Fact]
    public async Task ImportFromScryfall_Success_PersistsAndReturnsSource()
    {
        FakeGateway gateway = new(
            new ScryfallImportSource("default_cards", "Default Cards", "2026-09-28T09:05:39Z", "https://data.test/cards.jsonl.gz", 10),
            [Card("White One", "Creature — Human", "{W}", ["W"])]);

        var (factory, root) = CreateFactory(gateway);
        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.PostAsync("/api/catalog/import-from-scryfall", content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            JsonElement payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            payload.GetProperty("uniqueAthletes").GetInt32().ShouldBe(1);
            payload.GetProperty("sourceType").GetString().ShouldBe("default_cards");
            payload.GetProperty("sourceUpdatedAt").GetString().ShouldBe("2026-09-28T09:05:39Z");

            using HttpResponseMessage stats = await client.GetAsync("/api/catalog/stats");
            stats.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportFromScryfall_Unavailable_ReturnsBadGateway()
    {
        FakeGateway gateway = FakeGateway.ThrowingMetadata(
            new ScryfallUnavailableException("Scryfall bulk data is unavailable. Check your internet connection and try again."));

        var (factory, root) = CreateFactory(gateway);
        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.PostAsync("/api/catalog/import-from-scryfall", content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
            string body = await response.Content.ReadAsStringAsync();
            body.ShouldContain("Scryfall");
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportFromScryfall_Malformed_ReturnsBadRequest_AndLeavesCatalogIntact()
    {
        var (seedFactory, seedRoot) = CreateFactory(FakeGateway.WithRecords([Card("White One", "Creature — Human", "{W}", ["W"])]));
        try
        {
            using HttpClient seedClient = seedFactory.CreateClient();
            using HttpResponseMessage seeded = await seedClient.PostAsync("/api/catalog/import-from-scryfall", content: null);
            seeded.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            seedFactory.Dispose();
        }

        FakeGateway failing = FakeGateway.ThrowingDownload(new ScryfallImportFailedException("bad file"));
        var (factory, root) = CreateFactoryWithCatalogPath(failing, Path.Combine(seedRoot, "catalog.db"));
        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.PostAsync("/api/catalog/import-from-scryfall", content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            using HttpResponseMessage stats = await client.GetAsync("/api/catalog/stats");
            JsonElement payload = await stats.Content.ReadFromJsonAsync<JsonElement>();
            payload.GetProperty("totalAthletes").GetInt32().ShouldBe(1);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(seedRoot, recursive: true);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImportFromScryfall_QuotaInsufficient_ReturnsUnprocessable()
    {
        List<BulkCardRecord> healthy = [];
        for (int i = 0; i < 256; i++)
        {
            healthy.Add(Card($"White {i:0000}", "Creature — Human", "{W}", ["W"]));
            healthy.Add(Card($"Blue {i:0000}", "Creature — Merfolk", "{U}", ["U"]));
            healthy.Add(Card($"Black {i:0000}", "Creature — Vampire", "{B}", ["B"]));
            healthy.Add(Card($"Red {i:0000}", "Creature — Goblin", "{R}", ["R"]));
            healthy.Add(Card($"Green {i:0000}", "Creature — Elf", "{G}", ["G"]));
            healthy.Add(Card($"Multi {i:0000}", "Creature — Human Wizard", "{W}{U}", ["W", "U"]));
            healthy.Add(Card($"Hybrid {i:0000}", "Creature — Ouphe", "{W/U}", ["W", "U"]));
            healthy.Add(Card($"Gray {i:0000}", "Artifact Creature — Golem", "{0}", []));
        }

        var (seedFactory, seedRoot) = CreateFactory(FakeGateway.WithRecords(healthy));
        try
        {
            using HttpClient seedClient = seedFactory.CreateClient();
            using HttpResponseMessage seeded = await seedClient.PostAsync("/api/catalog/import-from-scryfall", content: null);
            seeded.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            seedFactory.Dispose();
        }

        FakeGateway thin = FakeGateway.WithRecords([Card("Only White", "Creature — Human", "{W}", ["W"])]);
        var (factory, root) = CreateFactoryWithCatalogPath(thin, Path.Combine(seedRoot, "catalog.db"));
        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.PostAsync("/api/catalog/import-from-scryfall", content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            string body = await response.Content.ReadAsStringAsync();
            body.ShouldContain("256");

            using HttpResponseMessage stats = await client.GetAsync("/api/catalog/stats");
            JsonElement payload = await stats.Content.ReadFromJsonAsync<JsonElement>();
            payload.GetProperty("totalAthletes").GetInt32().ShouldBe(2048);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(seedRoot, recursive: true);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RawJsonArrayImport_StillWorks_AfterScryfallSlice()
    {
        var (factory, root) = CreateFactory(FakeGateway.WithRecords([]));
        try
        {
            using HttpClient client = factory.CreateClient();
            string bulkJson = """
                [
                  {"name": "White One", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
                ]
                """;
            using StringContent content = new(bulkJson, System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync("/api/catalog/import", content);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static BulkCardRecord Card(string name, string typeLine, string mana, string[] colors) => new()
    {
        Name = name,
        Layout = "normal",
        TypeLine = typeLine,
        ManaCost = mana,
        Colors = colors,
        Keywords = [],
        OracleText = string.Empty,
        Set = "tst",
    };

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory(IScryfallBulkGateway gateway)
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-scryfall-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
                services.AddSingleton(gateway);
            }));
        return (factory, root);
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactoryWithCatalogPath(
        IScryfallBulkGateway gateway,
        string catalogPath)
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-scryfall-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = catalogPath);
                services.AddSingleton(gateway);
            }));
        return (factory, root);
    }

    private sealed class FakeGateway : IScryfallBulkGateway
    {
        private readonly ScryfallImportSource _source;
        private readonly IReadOnlyList<BulkCardRecord> _records;
        private readonly Exception? _metadataError;
        private readonly Exception? _downloadError;

        public FakeGateway(ScryfallImportSource source, IReadOnlyList<BulkCardRecord> records)
            : this(source, records, null, null)
        {
        }

        private FakeGateway(
            ScryfallImportSource source,
            IReadOnlyList<BulkCardRecord> records,
            Exception? metadataError,
            Exception? downloadError)
        {
            _source = source;
            _records = records;
            _metadataError = metadataError;
            _downloadError = downloadError;
        }

        public static FakeGateway WithRecords(IReadOnlyList<BulkCardRecord> records) => new(
            new ScryfallImportSource("default_cards", "Default Cards", null, "https://data.test/cards.jsonl.gz", null),
            records);

        public static FakeGateway ThrowingMetadata(Exception error) => new(
            new ScryfallImportSource("default_cards", "Default Cards", null, "https://data.test/cards.jsonl.gz", null),
            [],
            metadataError: error,
            downloadError: null);

        public static FakeGateway ThrowingDownload(Exception error) => new(
            new ScryfallImportSource("default_cards", "Default Cards", null, "https://data.test/cards.jsonl.gz", null),
            [],
            metadataError: null,
            downloadError: error);

        public Task<ScryfallImportSource> GetDefaultCardsSourceAsync(CancellationToken cancellationToken = default)
        {
            if (_metadataError is not null)
            {
                throw _metadataError;
            }

            return Task.FromResult(_source);
        }

        public Task<IReadOnlyList<BulkCardRecord>> DownloadCardsAsync(ScryfallImportSource source, CancellationToken cancellationToken = default)
        {
            if (_downloadError is not null)
            {
                throw _downloadError;
            }

            return Task.FromResult(_records);
        }
    }
}
