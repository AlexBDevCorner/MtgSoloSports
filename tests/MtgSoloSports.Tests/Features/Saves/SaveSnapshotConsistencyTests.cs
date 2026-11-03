using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
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
/// MSS-126 regression: exports and recovery checkpoints must be built from one
/// consistent committed snapshot. A pinned WAL reader plus a later committed
/// write must not produce a success artifact whose manifest describes newer
/// data than the embedded database. Uses only a fresh universe plus a metadata
/// update; no full-season setup.
/// </summary>
public sealed class SaveSnapshotConsistencyTests
{
    [Fact]
    public async Task Export_WithPinnedReader_IncludesCommittedBoundary()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Before", 1001UL, 2002UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            string livePath = store.GetSaveFilePath(saveId);

            SqliteConnectionStringBuilder builder = new() { DataSource = livePath, Pooling = false };
            using SqliteConnection reader = new(builder.ToString());
            await reader.OpenAsync();
            await BeginDeferredAsync(reader);
            (await ReadLiveNameAsync(reader)).ShouldBe("Before");
            await CommitNameAsync(livePath, "After");
            SqliteConnection.ClearAllPools();

            SaveStore.ExportRecord? exported = await TryExportAsync(store, saveId, livePath);
            if (exported is null)
            {
                return;
            }

            await AssertExportMatchesAfterAsync(exported);
            (await ReadLiveNameAsync(reader)).ShouldBe("Before");
            (await ReadCommittedNameAsync(livePath)).ShouldBe("After");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Checkpoint_WithPinnedReader_IncludesCommittedBoundary_AndPreservesPrior()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Before", 3003UL, 4004UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            string livePath = store.GetSaveFilePath(saveId);
            SaveStore.CheckpointRecord prior = await store.CreateCheckpointAsync(saveId, "prior");

            SqliteConnectionStringBuilder builder = new() { DataSource = livePath, Pooling = false };
            using SqliteConnection reader = new(builder.ToString());
            await reader.OpenAsync();
            await BeginDeferredAsync(reader);
            (await ReadLiveNameAsync(reader)).ShouldBe("Before");
            await CommitNameAsync(livePath, "After");
            SqliteConnection.ClearAllPools();

            SaveStore.CheckpointRecord? latest = await TryCheckpointAsync(store, saveId, prior, root);
            if (latest is null)
            {
                return;
            }

            await AssertCheckpointMatchesAfterAsync(root, saveId, latest);
            IReadOnlyList<SaveStore.CheckpointRecord> all = await store.ListCheckpointsAsync(saveId);
            all.Count.ShouldBe(2);
            all.Any(e => e.CheckpointId == prior.CheckpointId && string.Equals(e.SaveName, "Before", StringComparison.Ordinal)).ShouldBeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Export_ConcurrentMutation_RemainsSelfConsistent()
    {
        // Interleaving coverage: mutations racing the metadata/hash/copy steps
        // must never yield a success artifact whose manifest disagrees with its
        // embedded database. Every export observes one committed snapshot.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Base", 5005UL, 6006UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            for (int i = 0; i < 5; i++)
            {
                await AssertSequentialExportConsistentAsync(store, saveId, i);
            }

            await AssertOverlappingExportConsistentAsync(store, saveId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<SaveStore.ExportRecord?> TryExportAsync(SaveStore store, Guid saveId, string livePath)
    {
        try
        {
            return await store.ExportAsync(saveId).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Explicit snapshot failure without a success artifact is allowed:
            // live data must stay intact.
            (await ReadCommittedNameAsync(livePath).ConfigureAwait(false)).ShouldBe("After");
            return null;
        }
    }

    private static async Task AssertExportMatchesAfterAsync(SaveStore.ExportRecord exported)
    {
        exported.Manifest.Name.ShouldBe("After");
        (SaveBundleManifest manifest, byte[] databaseBytes, int _) = Unpack(exported.ZipBytes);
        manifest.ShouldBe(exported.Manifest);
        manifest.DatabaseSha256.ShouldBe(SaveBundle.ComputeBytesSha256Hex(databaseBytes));
        string embeddedName = await ReadNameFromDatabaseBytesAsync(databaseBytes).ConfigureAwait(false);
        embeddedName.ShouldBe("After");
        embeddedName.ShouldBe(manifest.Name);
    }

    private static async Task<SaveStore.CheckpointRecord?> TryCheckpointAsync(
        SaveStore store,
        Guid saveId,
        SaveStore.CheckpointRecord prior,
        string root)
    {
        try
        {
            return await store.CreateCheckpointAsync(saveId, "pinned").ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            IReadOnlyList<SaveStore.CheckpointRecord> listed = await store.ListCheckpointsAsync(saveId).ConfigureAwait(false);
            listed.Count.ShouldBe(1);
            listed[0].ShouldBe(prior);
            string priorDb = SaveCheckpointFiles.GetCheckpointDatabasePath(
                SaveCheckpointFiles.GetSaveCheckpointDirectory(root, saveId), prior.CheckpointId);
            (await ReadNameFromDatabaseFileAsync(priorDb).ConfigureAwait(false)).ShouldBe("Before");
            return null;
        }
    }

    private static async Task AssertCheckpointMatchesAfterAsync(string root, Guid saveId, SaveStore.CheckpointRecord latest)
    {
        latest.SaveName.ShouldBe("After");
        latest.DatabaseSha256.Length.ShouldBe(64);
        string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, saveId);
        string latestDb = SaveCheckpointFiles.GetCheckpointDatabasePath(directory, latest.CheckpointId);
        string sidecarPath = SaveCheckpointFiles.GetCheckpointSidecarPath(directory, latest.CheckpointId);
        (await ReadNameFromDatabaseFileAsync(latestDb).ConfigureAwait(false)).ShouldBe("After");
        SaveBundle.ComputeFileSha256Hex(latestDb).ShouldBe(latest.DatabaseSha256);
        SaveCheckpointSidecar sidecar = SaveCheckpointSidecar.FromJson(await File.ReadAllTextAsync(sidecarPath).ConfigureAwait(false));
        sidecar.DatabaseSha256.ShouldBe(latest.DatabaseSha256);
        sidecar.SaveName.ShouldBe("After");
    }

    private static async Task AssertSequentialExportConsistentAsync(SaveStore store, Guid saveId, int index)
    {
        string mutatedName = $"Mutated-{index}";
        await MutateLiveSaveNameAsync(store, saveId, mutatedName).ConfigureAwait(false);
        SaveStore.ExportRecord exported = await store.ExportAsync(saveId).ConfigureAwait(false);
        (SaveBundleManifest manifest, byte[] databaseBytes, int _) = Unpack(exported.ZipBytes);
        manifest.DatabaseSha256.ShouldBe(SaveBundle.ComputeBytesSha256Hex(databaseBytes));
        string embeddedName = await ReadNameFromDatabaseBytesAsync(databaseBytes).ConfigureAwait(false);
        embeddedName.ShouldBe(manifest.Name);
    }

    private static async Task AssertOverlappingExportConsistentAsync(SaveStore store, Guid saveId)
    {
        Task mutate = MutateLiveSaveNameAsync(store, saveId, "Raced");
        Task<SaveStore.ExportRecord> export = store.ExportAsync(saveId);
        await Task.WhenAll(mutate, export).ConfigureAwait(false);
        SaveStore.ExportRecord raced = await export.ConfigureAwait(false);
        (SaveBundleManifest racedManifest, byte[] racedBytes, int _) = Unpack(raced.ZipBytes);
        racedManifest.DatabaseSha256.ShouldBe(SaveBundle.ComputeBytesSha256Hex(racedBytes));
        (await ReadNameFromDatabaseBytesAsync(racedBytes).ConfigureAwait(false)).ShouldBe(racedManifest.Name);
    }

    private static async Task BeginDeferredAsync(SqliteConnection reader)
    {
        using SqliteCommand begin = reader.CreateCommand();
        begin.CommandText = "BEGIN DEFERRED;";
        _ = await begin.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<string> ReadLiveNameAsync(SqliteConnection reader)
    {
        using SqliteCommand select = reader.CreateCommand();
        select.CommandText = "SELECT Name FROM SaveMetadata WHERE Id = 1;";
        return (string)(await select.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task CommitNameAsync(string livePath, string name)
    {
        SqliteConnectionStringBuilder writerBuilder = new() { DataSource = livePath, Pooling = false };
        using SqliteConnection writer = new(writerBuilder.ToString());
        await writer.OpenAsync().ConfigureAwait(false);
        using (SqliteCommand busy = writer.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=5000;";
            _ = await busy.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using (SqliteCommand update = writer.CreateCommand())
        {
            update.CommandText = $"UPDATE SaveMetadata SET Name = '{name}' WHERE Id = 1;";
            _ = await update.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadCommittedNameAsync(string livePath)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = livePath, Pooling = false };
        using SqliteConnection live = new(builder.ToString());
        await live.OpenAsync().ConfigureAwait(false);
        using SqliteCommand check = live.CreateCommand();
        check.CommandText = "SELECT Name FROM SaveMetadata WHERE Id = 1;";
        return (string)(await check.ExecuteScalarAsync().ConfigureAwait(false))!;
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

    private static async Task<string> ReadNameFromDatabaseBytesAsync(byte[] databaseBytes)
    {
        string temp = Path.Combine(Path.GetTempPath(), "mtgsolosports-snap-" + Guid.NewGuid().ToString("N") + ".db");
        await File.WriteAllBytesAsync(temp, databaseBytes).ConfigureAwait(false);
        try
        {
            return await ReadNameFromDatabaseFileAsync(temp).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTempDatabase(temp);
        }
    }

    private static void DeleteTempDatabase(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (IOException)
        {
        }

        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            try
            {
                File.Delete(temp + suffix);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<string> ReadNameFromDatabaseFileAsync(string databasePath)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = databasePath, Pooling = false };
        using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM SaveMetadata WHERE Id = 1;";
        return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-snapshot-" + Guid.NewGuid().ToString("N"));
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
