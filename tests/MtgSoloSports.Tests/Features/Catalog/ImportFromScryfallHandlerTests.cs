using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Features.Catalog.ImportFromScryfall;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.SimulationKernel.Catalog;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Catalog;

public sealed class ImportFromScryfallHandlerTests
{
    [Fact]
    public async Task EmptyCatalog_SmallDataset_ImportsAndReportsInsufficient()
    {
        var (store, root) = CreateStore();
        try
        {
            FakeGateway gateway = new(
                new ScryfallImportSource("default_cards", "Default Cards", "2026-09-28T09:05:39Z", "https://data.test/cards.jsonl.gz", 10),
                [WhiteCard("White One"), BlueCard("Blue One")]);
            ImportFromScryfallHandler handler = new(gateway, store, new ScryfallImportLock());

            ImportFromScryfallResponse response = await handler.HandleAsync();

            response.UniqueAthletes.ShouldBe(2);
            response.IsSufficientForSave.ShouldBeFalse();
            response.SourceType.ShouldBe("default_cards");
            response.SourceUpdatedAt.ShouldBe("2026-09-28T09:05:39Z");
            (await store.GetTotalAsync()).ShouldBe(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FullDataset_ImportsAndBecomesSufficient()
    {
        var (store, root) = CreateStore();
        try
        {
            List<BulkCardRecord> records = QuotaSufficientRecords();
            FakeGateway gateway = new(
                new ScryfallImportSource("default_cards", "Default Cards", "2026-09-28T09:05:39Z", "https://data.test/cards.jsonl.gz", 10),
                records);
            ImportFromScryfallHandler handler = new(gateway, store, new ScryfallImportLock());

            ImportFromScryfallResponse response = await handler.HandleAsync();

            response.IsSufficientForSave.ShouldBeTrue();
            response.UniqueAthletes.ShouldBe(2048);
            foreach (SportingColor color in Enum.GetValues<SportingColor>())
            {
                response.CountsBySportingColor[color].ShouldBe(256);
            }

            await store.EnsureSufficientForSaveAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QuotaInadequate_WithHealthyExisting_LeavesCatalogIntact()
    {
        var (store, root) = CreateStore();
        try
        {
            ImportCatalogHandler seed = new(store);
            _ = await seed.HandleAsync(new ImportCatalogRequest(JsonArray(QuotaSufficientRecords())));

            (await store.GetTotalAsync()).ShouldBe(2048);
            IReadOnlyDictionary<SportingColor, int> before = await store.GetCountsAsync();

            FakeGateway gateway = new(
                new ScryfallImportSource("default_cards", "Default Cards", "t", "https://data.test/cards.jsonl.gz", null),
                [WhiteCard("Only White")]);
            ImportFromScryfallHandler handler = new(gateway, store, new ScryfallImportLock());

            CatalogQuotaInsufficientException ex = await Should.ThrowAsync<CatalogQuotaInsufficientException>(
                () => handler.HandleAsync());
            ex.Insufficient.Count.ShouldBeGreaterThan(0);

            IReadOnlyDictionary<SportingColor, int> after = await store.GetCountsAsync();
            after[SportingColor.White].ShouldBe(before[SportingColor.White]);
            (await store.GetTotalAsync()).ShouldBe(2048);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyDownload_LeavesCatalogIntact()
    {
        var (store, root) = CreateStore();
        try
        {
            ImportCatalogHandler seed = new(store);
            _ = await seed.HandleAsync(new ImportCatalogRequest(JsonArray([WhiteCard("White One")])));

            FakeGateway gateway = new(
                new ScryfallImportSource("default_cards", "Default Cards", null, "https://data.test/cards.jsonl.gz", null),
                []);
            ImportFromScryfallHandler handler = new(gateway, store, new ScryfallImportLock());

            await Should.ThrowAsync<ScryfallImportFailedException>(() => handler.HandleAsync());
            (await store.GetTotalAsync()).ShouldBe(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledDownload_LeavesCatalogIntact()
    {
        var (store, root) = CreateStore();
        try
        {
            ImportCatalogHandler seed = new(store);
            _ = await seed.HandleAsync(new ImportCatalogRequest(JsonArray([WhiteCard("White One")])));

            FakeGateway gateway = new(
                new ScryfallImportSource("default_cards", "Default Cards", null, "https://data.test/cards.jsonl.gz", null),
                [WhiteCard("White Two")],
                throwOnDownload: new OperationCanceledException("cancelled"));
            ImportFromScryfallHandler handler = new(gateway, store, new ScryfallImportLock());

            await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync());
            (await store.GetTotalAsync()).ShouldBe(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentImports_SecondFailsFast()
    {
        var (store, root) = CreateStore();
        try
        {
            SlowGateway gateway = new();
            ScryfallImportLock sharedLock = new();
            ImportFromScryfallHandler first = new(gateway, store, sharedLock);
            ImportFromScryfallHandler second = new(gateway, store, sharedLock);

            Task<ImportFromScryfallResponse> running = first.HandleAsync();
            await Task.Delay(50);
            await Should.ThrowAsync<ScryfallImportInProgressException>(() => second.HandleAsync());

            gateway.ReleaseDownload();
            ImportFromScryfallResponse response = await running;
            response.UniqueAthletes.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (CatalogStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-scryfall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<CatalogStorageOptions> options = Options.Create(new CatalogStorageOptions { CatalogPath = Path.Combine(root, "catalog.db") });
        CatalogDbContextFactory factory = new();
        CatalogStore store = new(options, new TestHostEnvironment(root), factory);
        return (store, root);
    }

    private static BulkCardRecord WhiteCard(string name) => new()
    {
        Name = name,
        Layout = "normal",
        TypeLine = "Creature — Human",
        ManaCost = "{W}",
        Colors = ["W"],
        Keywords = [],
        OracleText = string.Empty,
        Set = "tst",
    };

    private static BulkCardRecord BlueCard(string name) => new()
    {
        Name = name,
        Layout = "normal",
        TypeLine = "Creature — Human",
        ManaCost = "{U}",
        Colors = ["U"],
        Keywords = [],
        OracleText = string.Empty,
        Set = "tst",
    };

    private static List<BulkCardRecord> QuotaSufficientRecords()
    {
        List<BulkCardRecord> records = new(2048);
        for (int i = 0; i < 256; i++)
        {
            records.Add(ColorCard($"White Athlete {i:0000}", "{W}", ["W"], "Creature — Human"));
            records.Add(ColorCard($"Blue Athlete {i:0000}", "{U}", ["U"], "Creature — Merfolk"));
            records.Add(ColorCard($"Black Athlete {i:0000}", "{B}", ["B"], "Creature — Vampire"));
            records.Add(ColorCard($"Red Athlete {i:0000}", "{R}", ["R"], "Creature — Goblin"));
            records.Add(ColorCard($"Green Athlete {i:0000}", "{G}", ["G"], "Creature — Elf"));
            records.Add(ColorCard($"Multicolor Athlete {i:0000}", "{W}{U}", ["W", "U"], "Creature — Human Wizard"));
            records.Add(ColorCard($"Hybrid Athlete {i:0000}", "{W/U}", ["W", "U"], "Creature — Ouphe"));
            records.Add(ColorCard($"Colorless Athlete {i:0000}", "{0}", [], "Artifact Creature — Golem"));
        }

        return records;
    }

    private static BulkCardRecord ColorCard(string name, string mana, string[] colors, string typeLine) => new()
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

    private static string JsonArray(IReadOnlyList<BulkCardRecord> records)
    {
        List<string> entries = new(records.Count);
        foreach (BulkCardRecord record in records)
        {
            string colors = "[" + string.Join(",", (record.Colors ?? []).Select(c => $"\"{c}\"")) + "]";
            entries.Add($"{{\"name\": \"{record.Name}\", \"layout\": \"normal\", \"type_line\": \"{record.TypeLine}\", \"mana_cost\": \"{record.ManaCost}\", \"colors\": {colors}, \"keywords\": [], \"oracle_text\": \"\", \"set\": \"tst\", \"image_uris\": null}}");
        }

        return "[" + string.Join(",", entries) + "]";
    }

    private sealed class FakeGateway : IScryfallBulkGateway
    {
        private readonly ScryfallImportSource _source;
        private readonly IReadOnlyList<BulkCardRecord> _records;
        private readonly Exception? _throwOnDownload;

        public FakeGateway(ScryfallImportSource source, IReadOnlyList<BulkCardRecord> records, Exception? throwOnDownload = null)
        {
            _source = source;
            _records = records;
            _throwOnDownload = throwOnDownload;
        }

        public Task<ScryfallImportSource> GetDefaultCardsSourceAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_source);
        }

        public Task<IReadOnlyList<BulkCardRecord>> DownloadCardsAsync(ScryfallImportSource source, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_throwOnDownload is not null)
            {
                throw _throwOnDownload;
            }

            return Task.FromResult(_records);
        }
    }

    private sealed class SlowGateway : IScryfallBulkGateway
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseDownload() => _gate.TrySetResult();

        public Task<ScryfallImportSource> GetDefaultCardsSourceAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScryfallImportSource("default_cards", "Default Cards", null, "https://data.test/cards.jsonl.gz", null));

        public async Task<IReadOnlyList<BulkCardRecord>> DownloadCardsAsync(ScryfallImportSource source, CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return [WhiteCard("Slow White")];
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
