using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Saves;

/// <summary>
/// Store-level integration tests for save export/import portability:
/// artifact shape, full round trip, overwrite protection, and rejection of
/// corrupt or incompatible bundles without touching existing saves.
/// </summary>
public sealed class SaveExportImportTests
{
    [Fact]
    public async Task Export_ContainsDatabaseAndManifest_WithMatchingChecksum()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Portable", 101UL, 202UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);

            exported.SaveId.ShouldBe(created.Detail.SaveId);
            exported.FileName.ShouldBe($"{created.Detail.SaveId:N}.mtgsave.zip");
            exported.ContentType.ShouldBe("application/zip");
            exported.ZipBytes.Length.ShouldBeGreaterThan(0);

            (SaveBundleManifest manifest, byte[] databaseBytes, int entryCount) = Unpack(exported.ZipBytes);
            entryCount.ShouldBe(2);
            manifest.SaveId.ShouldBe(created.Detail.SaveId);
            manifest.Name.ShouldBe("Portable");
            manifest.SchemaVersion.ShouldBe(SaveSchemaVersion.Current);
            manifest.RulesVersion.ShouldBe(created.Detail.RulesVersion);
            manifest.DatabaseSha256.ShouldBe(SaveBundle.ComputeBytesSha256Hex(databaseBytes));
            manifest.DatabaseSha256.ShouldBe(exported.Manifest.DatabaseSha256);
            exported.Manifest.ShouldBe(manifest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportImport_RoundTrip_PreservesUniverse()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("RoundTrip", 303UL, 404UL, UniverseTestCatalog.Build());
            List<string> namesBefore = await ReadAthleteNamesAsync(store, created.Detail.SaveId);
            namesBefore.Count.ShouldBe(2048);
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);

            await store.DeleteAsync(created.Detail.SaveId);
            SaveStore.SaveDetailRecord imported = await store.ImportAsync(exported.ZipBytes, overwrite: false);

            imported.ShouldBe(created.Detail);
            List<string> namesAfter = await ReadAthleteNamesAsync(store, imported.SaveId);
            namesAfter.ShouldBe(namesBefore);
            await AssertColorQuotasAsync(store, imported.SaveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_ExistingSave_WithoutOverwrite_ThrowsConflict_PreservesOriginal()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Original", 505UL, 606UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);

            await Should.ThrowAsync<SaveAlreadyExistsException>(() => store.ImportAsync(exported.ZipBytes, overwrite: false));

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.Detail.SaveId);
            reopened.ShouldBe(created.Detail);
            (await store.ListCheckpointsAsync(created.Detail.SaveId)).Count.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_WithExplicitOverwrite_CheckpointsThenReplaces()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Original", 707UL, 808UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            await MutateLiveSaveNameAsync(store, created.Detail.SaveId, "Mutated");

            SaveStore.SaveDetailRecord replaced = await store.ImportAsync(exported.ZipBytes, overwrite: true);
            replaced.Name.ShouldBe("Original");
            replaced.ShouldBe(created.Detail);

            IReadOnlyList<SaveStore.CheckpointRecord> checkpoints = await store.ListCheckpointsAsync(created.Detail.SaveId);
            checkpoints.Count.ShouldBe(1);
            checkpoints[0].Reason.ShouldBe("pre-import-overwrite");
            checkpoints[0].SaveName.ShouldBe("Mutated");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_TamperedDatabase_IsRejected_AndCreatesNoSave()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Victim", 909UL, 910UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            (SaveBundleManifest manifest, byte[] databaseBytes, int _) = Unpack(exported.ZipBytes);
            databaseBytes[200] ^= 0xFF;
            byte[] tampered = Repack(manifest, databaseBytes);

            await store.DeleteAsync(created.Detail.SaveId);
            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() => store.ImportAsync(tampered, overwrite: false));
            failure.Message.ShouldContain("checksum");

            Directory.GetFiles(root, "*.db").Length.ShouldBe(0);
            (await store.ListAsync()).Count.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_FutureSchema_IsRejected_AndCreatesNoSave()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Old", 911UL, 912UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            (SaveBundleManifest manifest, byte[] databaseBytes, int _) = Unpack(exported.ZipBytes);
            byte[] future = Repack(manifest with { SchemaVersion = SaveSchemaVersion.Current + 1 }, databaseBytes);

            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() => store.ImportAsync(future, overwrite: true));
            failure.Message.ShouldContain("newer than supported");

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.Detail.SaveId);
            reopened.ShouldBe(created.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_FutureRules_IsRejected()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Rules", 913UL, 914UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            (SaveBundleManifest manifest, byte[] databaseBytes, int _) = Unpack(exported.ZipBytes);
            byte[] futureRules = Repack(manifest with { RulesVersion = 999 }, databaseBytes);

            await store.DeleteAsync(created.Detail.SaveId);
            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() => store.ImportAsync(futureRules, overwrite: false));
            failure.Message.ShouldContain("rules version");
            Directory.GetFiles(root, "*.db").Length.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_NotZip_IsRejected()
    {
        var (store, root) = CreateStore();
        try
        {
            _ = await store.CreateAsync("Keepsake", 915UL, 916UL, UniverseTestCatalog.Build());
            byte[] garbage = Encoding.UTF8.GetBytes("this is not a save bundle");
            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() => store.ImportAsync(garbage, overwrite: false));
            failure.Message.ShouldContain("not a valid save bundle");
            (await store.ListAsync()).Count.ShouldBe(1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Export_UnknownSave_ThrowsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            await Should.ThrowAsync<SaveNotFoundException>(() => store.ExportAsync(Guid.NewGuid()));
            await Should.ThrowAsync<ArgumentException>(() => store.ImportAsync([], overwrite: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (SaveBundleManifest Manifest, byte[] DatabaseBytes, int EntryCount) Unpack(byte[] zipBytes)
    {
        using ZipArchive archive = new(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
        int count = archive.Entries.Count;
        ZipArchiveEntry databaseEntry = archive.Entries.Single(e => string.Equals(e.FullName, SaveBundleManifest.DatabaseEntryName, StringComparison.Ordinal));
        ZipArchiveEntry manifestEntry = archive.Entries.Single(e => string.Equals(e.FullName, SaveBundleManifest.ManifestEntryName, StringComparison.Ordinal));
        using StreamReader reader = new(manifestEntry.Open(), Encoding.UTF8);
        SaveBundleManifest manifest = SaveBundleManifest.FromJson(reader.ReadToEnd());
        using MemoryStream databaseBuffer = new();
        using (Stream databaseStream = databaseEntry.Open())
        {
            databaseStream.CopyTo(databaseBuffer);
        }

        return (manifest, databaseBuffer.ToArray(), count);
    }

    private static byte[] Repack(SaveBundleManifest manifest, byte[] databaseBytes)
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry databaseEntry = archive.CreateEntry(SaveBundleManifest.DatabaseEntryName);
            using (Stream stream = databaseEntry.Open())
            {
                stream.Write(databaseBytes, 0, databaseBytes.Length);
            }

            byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJson());
            ZipArchiveEntry manifestEntry = archive.CreateEntry(SaveBundleManifest.ManifestEntryName);
            using (Stream stream = manifestEntry.Open())
            {
                stream.Write(manifestBytes, 0, manifestBytes.Length);
            }
        }

        return buffer.ToArray();
    }

    private static async Task<List<string>> ReadAthleteNamesAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes
            .AsNoTracking()
            .OrderBy(e => e.Name)
            .Select(e => e.Name)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    private static async Task AssertColorQuotasAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        (await context.SaveAthletes.CountAsync().ConfigureAwait(false)).ShouldBe(2048);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            int count = await context.SaveAthletes.CountAsync(e => e.SportingColor == (int)color).ConfigureAwait(false);
            count.ShouldBe(256);
        }
    }

    private static async Task MutateLiveSaveNameAsync(SaveStore store, Guid saveId, string newName)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await context.SaveMetadata.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        metadata.Name = newName;
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-export-" + Guid.NewGuid().ToString("N"));
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
