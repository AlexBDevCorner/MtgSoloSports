using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Saves;

/// <summary>
/// MSS-125 Windows file-sharing regressions: export, checkpoint, delete,
/// import overwrite and checkpoint restore must work after ordinary pooled
/// reads/writes have disposed their contexts. Tests exercise production
/// <see cref="SaveStore"/> operations without manually clearing SQLite pools
/// before the action under test; pooled handles are only cleared during test
/// cleanup for isolation. On the audited revision these fail on Windows with
/// sharing violations (File.Delete before pool clear, File.OpenRead with
/// Share.Read while pooled handles allow writes).
/// Fresh saves only; no seasons are simulated.
/// </summary>
public sealed class SaveWindowsFileSharingTests
{
    [Fact]
    public async Task CreateOpenDelete_Succeeds_AffectsOnlyThatSave()
    {
        // MSS-125: create/open/delete must succeed on Windows after ordinary
        // pooled reads and affect only that save.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord first = await store.CreateAsync("First", 1101UL, 1102UL, UniverseTestCatalog.Build());
            SaveStore.CreationRecord second = await store.CreateAsync("Second", 1103UL, 1104UL, UniverseTestCatalog.Build());

            // Ordinary pooled reads: contexts are disposed but pooled handles remain.
            _ = await store.OpenAsync(first.Detail.SaveId);
            _ = await store.OpenAsync(second.Detail.SaveId);
            await ReadAthleteCountAsync(store, first.Detail.SaveId);
            await ReadAthleteCountAsync(store, second.Detail.SaveId);

            // No manual pool clearing before the action under test.
            await store.DeleteAsync(first.Detail.SaveId);

            File.Exists(store.GetSaveFilePath(first.Detail.SaveId)).ShouldBeFalse();
            File.Exists(store.GetSaveFilePath(second.Detail.SaveId)).ShouldBeTrue();
            (await store.ListAsync()).Count.ShouldBe(1);
            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(second.Detail.SaveId);
            reopened.ShouldBe(second.Detail);
            await Should.ThrowAsync<SaveNotFoundException>(() => store.OpenAsync(first.Detail.SaveId));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Export_AfterPooledReads_Succeeds_WithMatchingChecksum()
    {
        // MSS-125: normal export must work with normally disposed pooled
        // SQLite connections; hashing/bundling allows concurrent pooled handles.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Portable", 1201UL, 1202UL, UniverseTestCatalog.Build());

            _ = await store.OpenAsync(created.Detail.SaveId);
            await ReadAthleteCountAsync(store, created.Detail.SaveId);
            await MutateLiveSaveNameAsync(store, created.Detail.SaveId, "Touched");
            _ = await store.OpenAsync(created.Detail.SaveId);

            SaveStore.SaveDetailRecord touched = await store.OpenAsync(created.Detail.SaveId);
            touched.Name.ShouldBe("Touched");

            // No manual pool clearing before export.
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);

            exported.SaveId.ShouldBe(created.Detail.SaveId);
            (SaveBundleManifest manifest, byte[] databaseBytes, _) = Unpack(exported.ZipBytes);
            manifest.ShouldBe(exported.Manifest);
            manifest.DatabaseSha256.ShouldBe(SaveBundle.ComputeBytesSha256Hex(databaseBytes));
            manifest.Name.ShouldBe("Touched");

            // Export never mutates live sporting state; WAL format is preserved.
            SaveStore.SaveDetailRecord live = await store.OpenAsync(created.Detail.SaveId);
            live.Name.ShouldBe("Touched");
            live.RngState.ShouldBe(created.Detail.RngState);
            live.RngStream.ShouldBe(created.Detail.RngStream);
            live.RulesJson.ShouldBe(created.Detail.RulesJson);
            (await ReadJournalModeAsync(store, created.Detail.SaveId)).ShouldBe("wal", StringCompareShould.IgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Checkpoint_AfterPooledReads_Succeeds_AndVerifies()
    {
        // MSS-125: checkpoint creation must work after ordinary pooled reads.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Checkpointed", 1301UL, 1302UL, UniverseTestCatalog.Build());
            _ = await store.OpenAsync(created.Detail.SaveId);
            await ReadAthleteCountAsync(store, created.Detail.SaveId);

            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "probe");

            checkpoint.SaveId.ShouldBe(created.Detail.SaveId);
            checkpoint.SaveName.ShouldBe("Checkpointed");
            string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, created.Detail.SaveId);
            string databasePath = SaveCheckpointFiles.GetCheckpointDatabasePath(directory, checkpoint.CheckpointId);
            File.Exists(databasePath).ShouldBeTrue();
            SaveBundle.ComputeFileSha256Hex(databasePath).ShouldBe(checkpoint.DatabaseSha256);

            SaveStore.SaveDetailRecord live = await store.OpenAsync(created.Detail.SaveId);
            live.ShouldBe(created.Detail);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportImport_RoundTrip_AfterPooledReads_PreservesUniverse()
    {
        // MSS-125: export/import round trip must work without clearing pools
        // and preserve WAL state, identities, RNG and rules.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("RoundTrip", 1401UL, 1402UL, UniverseTestCatalog.Build());
            List<string> namesBefore = await ReadAthleteNamesAsync(store, created.Detail.SaveId);
            _ = await store.OpenAsync(created.Detail.SaveId);

            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            await store.DeleteAsync(created.Detail.SaveId);

            SaveStore.SaveDetailRecord imported = await store.ImportAsync(exported.ZipBytes, overwrite: false);

            imported.ShouldBe(created.Detail);
            List<string> namesAfter = await ReadAthleteNamesAsync(store, imported.SaveId);
            namesAfter.ShouldBe(namesBefore);
            (await ReadJournalModeAsync(store, imported.SaveId)).ShouldBe("wal", StringCompareShould.IgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportOverwrite_AfterPooledReads_ReleasesHandles_AndReadsNewData()
    {
        // MSS-125: explicit import overwrite must safely release old handles
        // and subsequently read the new data, without clearing pools first.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Original", 1501UL, 1502UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            await MutateLiveSaveNameAsync(store, created.Detail.SaveId, "Mutated");
            _ = await store.OpenAsync(created.Detail.SaveId);
            await ReadAthleteCountAsync(store, created.Detail.SaveId);

            SaveStore.SaveDetailRecord replaced = await store.ImportAsync(exported.ZipBytes, overwrite: true);

            replaced.ShouldBe(created.Detail);
            replaced.Name.ShouldBe("Original");

            // Old pooled handles must not serve stale pages: a fresh read
            // without manual pool clearing sees the replaced data.
            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.Detail.SaveId);
            reopened.Name.ShouldBe("Original");
            reopened.ShouldBe(created.Detail);

            IReadOnlyList<SaveStore.CheckpointRecord> checkpoints = await store.ListCheckpointsAsync(created.Detail.SaveId);
            checkpoints.Count.ShouldBe(1);
            checkpoints[0].Reason.ShouldBe("pre-import-overwrite");
            checkpoints[0].SaveName.ShouldBe("Mutated");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckpointRestore_AfterPooledReads_ReplacesHandles_AndReadsNewData()
    {
        // MSS-125: checkpoint restore must safely replace old handles and
        // read the restored data after ordinary pooled reads, without clearing.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Steady", 1601UL, 1602UL, UniverseTestCatalog.Build());
            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-change");
            await MutateLiveSaveNameAsync(store, created.Detail.SaveId, "Changed");
            _ = await store.OpenAsync(created.Detail.SaveId);
            await ReadAthleteCountAsync(store, created.Detail.SaveId);

            SaveStore.SaveDetailRecord restored = await store.RestoreCheckpointAsync(created.Detail.SaveId, checkpoint.CheckpointId);

            restored.ShouldBe(created.Detail);
            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.Detail.SaveId);
            reopened.ShouldBe(created.Detail);
            reopened.Name.ShouldBe("Steady");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<int> ReadAthleteCountAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        return await context.SaveAthletes.CountAsync().ConfigureAwait(false);
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

    private static async Task MutateLiveSaveNameAsync(SaveStore store, Guid saveId, string newName)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await context.SaveMetadata.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        metadata.Name = newName;
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<string> ReadJournalModeAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        await context.Database.OpenConnectionAsync().ConfigureAwait(false);
        using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result?.ToString() ?? string.Empty;
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

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-winshare-" + Guid.NewGuid().ToString("N"));
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
