using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.SimulationKernel.Catalog;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Catalog;

public sealed class CatalogStoreTests
{
    [Fact]
    public async Task Import_PersistsSeparatelyFromSaves_AndReportsCounts()
    {
        var (store, root) = CreateStore();
        try
        {
            string json = """
                [
                  {"name": "White One", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null},
                  {"name": "Blue One", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{U}", "colors": ["U"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null},
                  {"name": "Bolt", "layout": "normal", "type_line": "Instant", "mana_cost": "{R}", "colors": ["R"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
                ]
                """;

            ImportCatalogHandler handler = new(store);
            ImportCatalogResponse response = await handler.HandleAsync(new ImportCatalogRequest(json));

            response.TotalPrintings.ShouldBe(3);
            response.EligiblePrintings.ShouldBe(2);
            response.UniqueAthletes.ShouldBe(2);
            response.SkippedNonCreature.ShouldBe(1);
            response.CountsBySportingColor[SportingColor.White].ShouldBe(1);
            response.CountsBySportingColor[SportingColor.Blue].ShouldBe(1);
            response.CountsBySportingColor[SportingColor.Red].ShouldBe(0);
            response.IsSufficientForSave.ShouldBeFalse();

            // Persisted separately from saves: exactly one catalog file, no save files.
            File.Exists(store.GetCatalogFilePath()).ShouldBeTrue();
            Directory.GetFiles(root, "*.db").Select(Path.GetFileName).ShouldBe(["catalog.db"]);

            IReadOnlyDictionary<SportingColor, int> counts = await store.GetCountsAsync();
            counts[SportingColor.White].ShouldBe(1);
            counts[SportingColor.Blue].ShouldBe(1);
            (await store.GetTotalAsync()).ShouldBe(2);

            // All eight colors always reported.
            counts.Count.ShouldBe(8);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reimport_ReplacesCatalogAtomically()
    {
        var (store, root) = CreateStore();
        try
        {
            ImportCatalogHandler handler = new(store);
            string first = """
                [
                  {"name": "White One", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
                ]
                """;
            string second = """
                [
                  {"name": "Black One", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{B}", "colors": ["B"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
                ]
                """;

            _ = await handler.HandleAsync(new ImportCatalogRequest(first));
            ImportCatalogResponse replaced = await handler.HandleAsync(new ImportCatalogRequest(second));

            replaced.UniqueAthletes.ShouldBe(1);
            replaced.CountsBySportingColor[SportingColor.White].ShouldBe(0);
            replaced.CountsBySportingColor[SportingColor.Black].ShouldBe(1);
            (await store.GetTotalAsync()).ShouldBe(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureSufficientForSave_ThrowsWhenAnyColorShort()
    {
        var (store, root) = CreateStore();
        try
        {
            ImportCatalogHandler handler = new(store);
            string json = """
                [
                  {"name": "White One", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
                ]
                """;
            _ = await handler.HandleAsync(new ImportCatalogRequest(json));

            InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => store.EnsureSufficientForSaveAsync());
            ex.Message.ShouldContain("256");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogStatsHandler_ReportsInventory()
    {
        var (store, root) = CreateStore();
        try
        {
            ImportCatalogHandler import = new(store);
            string json = """
                [
                  {"name": "Green One", "layout": "normal", "type_line": "Creature — Elf", "mana_cost": "{G}", "colors": ["G"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
                ]
                """;
            _ = await import.HandleAsync(new ImportCatalogRequest(json));

            MtgSoloSports.Features.Catalog.GetCatalogStats.GetCatalogStatsHandler stats = new(store);
            MtgSoloSports.Features.Catalog.GetCatalogStats.GetCatalogStatsResponse response = await stats.HandleAsync();

            response.TotalAthletes.ShouldBe(1);
            response.CountsBySportingColor[SportingColor.Green].ShouldBe(1);
            response.IsSufficientForSave.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ListAthletes_RoundTripsCandidatesForUniverseSelection()
    {
        var (store, root) = CreateStore();
        try
        {
            ImportCatalogHandler handler = new(store);
            string json = """
                [
                  {"name": "White One", "layout": "normal", "type_line": "Creature — Human Wizard", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": {"normal": "https://img/white.jpg"}},
                  {"name": "Blue One", "layout": "normal", "type_line": "Creature — Merfolk", "mana_cost": "{U}", "colors": ["U"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
                ]
                """;
            _ = await handler.HandleAsync(new ImportCatalogRequest(json));

            IReadOnlyList<CatalogAthlete> athletes = await store.ListAthletesAsync();
            athletes.Count.ShouldBe(2);

            CatalogAthlete white = athletes.Single(a => string.Equals(a.Name, "White One", StringComparison.Ordinal));
            white.SportingColor.ShouldBe(SportingColor.White);
            white.CreatureTypes.ShouldBe(["Human", "Wizard"]);
            white.ImageUrl.ShouldBe("https://img/white.jpg");
            white.ManaCost.ShouldBe("{W}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (CatalogStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<CatalogStorageOptions> options = Options.Create(new CatalogStorageOptions { CatalogPath = Path.Combine(root, "catalog.db") });
        TestHostEnvironment environment = new(root);
        CatalogDbContextFactory factory = new();
        CatalogStore store = new(options, environment, factory);
        return (store, root);
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
