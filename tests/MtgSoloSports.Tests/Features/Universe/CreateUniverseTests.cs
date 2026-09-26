using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Features.Universe.CreateUniverse;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Universe;

public sealed class CreateUniverseTests
{
    [Fact]
    public async Task SameSeed_ProducesIdenticalUniverse_Golden()
    {
        var (store, root) = CreateStore();
        try
        {
            IReadOnlyList<CatalogAthlete> catalog = UniverseTestCatalog.Build();
            SaveStore.CreationRecord first = await store.CreateAsync("Golden One", 9001UL, 7002UL, catalog);
            SaveStore.CreationRecord second = await store.CreateAsync("Golden Two", 9001UL, 7002UL, catalog);

            first.Universe.TotalAthletes.ShouldBe(2048);
            second.Universe.Checksum.ShouldBe(first.Universe.Checksum);
            second.Detail.RngState.ShouldBe(first.Detail.RngState);
            second.Detail.RngStream.ShouldBe(first.Detail.RngStream);

            // Same seed reproduces the selector output exactly.
            Pcg32V1 fresh = new(9001UL, 7002UL);
            UniverseSelection recomputed = UniverseSelector.Select(catalog, fresh, RulesV1.CreateDefault());
            recomputed.Summary.Checksum.ShouldBe(first.Universe.Checksum);
            fresh.Snapshot().State.ShouldBe(first.Detail.RngState);

            IReadOnlyList<string> firstNames = await SavedNamesAsync(store, first.Detail.SaveId);
            IReadOnlyList<string> secondNames = await SavedNamesAsync(store, second.Detail.SaveId);
            firstNames.ShouldBe(secondNames);
            firstNames.Count.ShouldBe(2048);
            firstNames.Distinct(StringComparer.Ordinal).Count().ShouldBe(2048);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DifferentSeed_ProducesDifferentButValidUniverse()
    {
        var (store, root) = CreateStore();
        try
        {
            IReadOnlyList<CatalogAthlete> catalog = UniverseTestCatalog.Build(perColor: 300);
            SaveStore.CreationRecord first = await store.CreateAsync("Seed A", 11UL, 12UL, catalog);
            SaveStore.CreationRecord second = await store.CreateAsync("Seed B", 13UL, 14UL, catalog);

            foreach (SaveStore.CreationRecord created in new[] { first, second })
            {
                created.Universe.TotalAthletes.ShouldBe(2048);
                foreach (SportingColor color in Enum.GetValues<SportingColor>())
                {
                    created.Universe.AthletesPerColor[color].ShouldBe(256);
                }

                (await SavedNamesAsync(store, created.Detail.SaveId)).Count.ShouldBe(2048);
            }

            string.Equals(second.Universe.Checksum, first.Universe.Checksum, StringComparison.Ordinal).ShouldBeFalse();

            IReadOnlyList<string> firstNames = await SavedNamesAsync(store, first.Detail.SaveId);
            IReadOnlyList<string> secondNames = await SavedNamesAsync(store, second.Detail.SaveId);
            secondNames.ShouldNotBe(firstNames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InsufficientCatalog_AbortsWithoutPartialSave()
    {
        var (store, root) = CreateStore();
        try
        {
            List<CatalogAthlete> catalog = [.. UniverseTestCatalog.Build()];
            // Remove five Red athletes so Red drops to 255.
            catalog.RemoveAll(a => a.SportingColor == SportingColor.Red && a.Name.EndsWith("0000", StringComparison.Ordinal));
            catalog.RemoveAll(a => a.SportingColor == SportingColor.Red && a.Name.EndsWith("0001", StringComparison.Ordinal));
            catalog.RemoveAll(a => a.SportingColor == SportingColor.Red && a.Name.EndsWith("0002", StringComparison.Ordinal));
            catalog.RemoveAll(a => a.SportingColor == SportingColor.Red && a.Name.EndsWith("0003", StringComparison.Ordinal));
            catalog.RemoveAll(a => a.SportingColor == SportingColor.Red && a.Name.EndsWith("0004", StringComparison.Ordinal));

            InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(
                () => store.CreateAsync("Short", 1UL, 2UL, catalog));
            ex.Message.ShouldContain("Red");
            ex.Message.ShouldContain("256");

            Directory.GetFiles(root, "*.db").ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SavedAthletes_CopyMetadataAndStartInCommonPool()
    {
        var (store, root) = CreateStore();
        try
        {
            IReadOnlyList<CatalogAthlete> catalog = UniverseTestCatalog.Build();
            Dictionary<string, CatalogAthlete> byName = catalog.ToDictionary(a => a.Name, StringComparer.Ordinal);
            SaveStore.CreationRecord created = await store.CreateAsync("Snapshot", 55UL, 66UL, catalog);

            using SaveDbContext context = store.OpenDbContext(created.Detail.SaveId);
            List<SaveAthleteEntity> saved = await context.SaveAthletes.AsNoTracking().ToListAsync();
            saved.Count.ShouldBe(2048);

            foreach (SaveAthleteEntity entity in saved)
            {
                entity.Status.ShouldBe((int)SaveAthleteStatus.CommonPool);
                byName.ShouldContainKey(entity.Name);
                CatalogAthlete candidate = byName[entity.Name];
                entity.SportingColor.ShouldBe((int)candidate.SportingColor);
                entity.ManaCost.ShouldBe(candidate.ManaCost);
                entity.TypeLine.ShouldBe(candidate.TypeLine);
                entity.ImageUrl.ShouldBe(candidate.ImageUrl);
                entity.SetCode.ShouldBe(candidate.SetCode);
                entity.FrontColors.ShouldBe(string.Concat(candidate.FrontColors));
                entity.IsArtifact.ShouldBe(candidate.IsArtifact);
                entity.HasDevoid.ShouldBe(candidate.HasDevoid);
                entity.HasHybridMana.ShouldBe(candidate.HasHybridMana);

                CatalogAthlete snapshot = entity.ToSnapshot();
                snapshot.Name.ShouldBe(candidate.Name);
                snapshot.SportingColor.ShouldBe(candidate.SportingColor);
                snapshot.CreatureTypes.ShouldBe(candidate.CreatureTypes);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogChanges_DoNotRewriteSavedUniverse()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Frozen", 77UL, 88UL, UniverseTestCatalog.Build());
            IReadOnlyList<string> before = await SavedNamesAsync(store, created.Detail.SaveId);

            // A later catalog with completely different names cannot touch the save.
            List<CatalogAthlete> replacement = [];
            foreach (SportingColor color in Enum.GetValues<SportingColor>())
            {
                for (int i = 10_000; i < 10_300; i++)
                {
                    replacement.Add(UniverseTestCatalog.BuildAthlete(color, i));
                }
            }

            SaveStore.CreationRecord other = await store.CreateAsync("Later", 77UL, 88UL, replacement);
            string.Equals(other.Universe.Checksum, created.Universe.Checksum, StringComparison.Ordinal).ShouldBeFalse();

            IReadOnlyList<string> after = await SavedNamesAsync(store, created.Detail.SaveId);
            after.ShouldBe(before);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Selector_ExactQuotas_SelectsEntireCatalog()
    {
        IReadOnlyList<CatalogAthlete> catalog = UniverseTestCatalog.Build(perColor: 256);

        Pcg32V1 firstRng = new(1UL, 2UL);
        UniverseSelection first = UniverseSelector.Select(catalog, firstRng, RulesV1.CreateDefault());

        Pcg32V1 secondRng = new(3UL, 4UL);
        UniverseSelection second = UniverseSelector.Select(catalog, secondRng, RulesV1.CreateDefault());

        // Every candidate is selected when the catalog exactly meets quotas,
        // so the set and checksum match even though the RNG states diverge.
        first.Selected.Count.ShouldBe(2048);
        first.Selected.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(catalog.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
        second.Summary.Checksum.ShouldBe(first.Summary.Checksum);
        secondRng.Snapshot().State.ShouldNotBe(firstRng.Snapshot().State);
    }

    [Fact]
    public void Selector_Checksum_IsOrderIndependent()
    {
        IReadOnlyList<CatalogAthlete> catalog = UniverseTestCatalog.Build(perColor: 256);
        UniverseSelection selection = UniverseSelector.Select(catalog, new Pcg32V1(9UL, 9UL), RulesV1.CreateDefault());

        List<CatalogAthlete> reversed = [.. selection.Selected];
        reversed.Reverse();
        UniverseSelector.ComputeChecksum(reversed).ShouldBe(selection.Summary.Checksum);
        selection.Summary.Checksum.Length.ShouldBe(64);
    }

    [Fact]
    public void Invariants_RejectCorruptedSelections()
    {
        IReadOnlyList<CatalogAthlete> catalog = UniverseTestCatalog.Build(perColor: 256);

        List<CatalogAthlete> duplicated = [.. catalog];
        duplicated[0] = duplicated[1];
        Should.Throw<InvalidOperationException>(
            () => UniverseInvariants.ValidateSelection(duplicated, 256, 2048));

        Should.Throw<InvalidOperationException>(
            () => UniverseInvariants.ValidateSelection(catalog.Take(2047).ToList(), 256, 2048));

        List<CatalogAthlete> missingColor = catalog.Where(a => a.SportingColor != SportingColor.Red).ToList();
        Should.Throw<InvalidOperationException>(
            () => UniverseInvariants.ValidateSelection(missingColor, 256, 2048));

        Should.Throw<InvalidOperationException>(
            () => UniverseSelector.Select(UniverseTestCatalog.Build(perColor: 10), new Pcg32V1(1UL, 2UL), RulesV1.CreateDefault()));
    }

    private static async Task<IReadOnlyList<string>> SavedNamesAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        List<string> names = await context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Name)
            .Select(e => e.Name)
            .ToListAsync().ConfigureAwait(false);
        return names;
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-universe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
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
