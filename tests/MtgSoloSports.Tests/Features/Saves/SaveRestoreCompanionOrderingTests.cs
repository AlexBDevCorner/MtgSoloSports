using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Data.Sqlite;
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
/// MSS-148 regressions: restore/import overwrite must never pair a freshly
/// replaced main database with the previous file's stale WAL. The tests craft
/// a crashed-writer state — a main database plus a WAL holding committed but
/// uncheckpointed frames — and prove the enforced ordering (companions die
/// before the new main moves in) from three angles: the window reader itself,
/// the production restore with a mid-restore observer, and an interrupted
/// replacement. Fresh saves plus metadata updates only; no simulated seasons.
/// </summary>
public sealed class SaveRestoreCompanionOrderingTests
{
    private sealed record CraftedSave(
        SaveStore.CreationRecord Created,
        Guid SaveId,
        string LivePath,
        SaveStore.CheckpointRecord Checkpoint,
        string CheckpointPath);

    [Fact]
    public async Task ReplacementOrdering_CompanionsDeletedBeforeMove_NeverPairsStaleWalWithNewMain()
    {
        // The reader opens the live path at the exact state each ordering
        // leaves behind once the new main database has moved in. Deleting the
        // replaced file's WAL/SHM companions only AFTER the move (the
        // pre-change ordering, phase 1) leaves a window in which the reader
        // pairs the new main with the stale WAL and reads the discarded
        // committed frames — the assertion that fails on that ordering.
        // Deleting the companions immediately BEFORE the move (the enforced
        // ordering, phase 2) leaves the same reader position with only the
        // intended checkpoint content.
        var (store, root) = CreateStore();
        try
        {
            CraftedSave save = await CreateCraftedSaveAsync(store, root, "Boundary", 9601UL, 9602UL);
            string stagingPath = Path.Combine(root, ".mss148-order.tmp");

            string foreign = await ReplaceMainInWindowAndReadAsync(save.LivePath, save.CheckpointPath, stagingPath, deleteCompanionsFirst: false);
            foreign.ShouldBe("Stale");

            string restored = await ReplaceMainInWindowAndReadAsync(save.LivePath, save.CheckpointPath, stagingPath, deleteCompanionsFirst: true);
            restored.ShouldBe("Boundary");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RestoreOverwrite_WithCommittedUncheckpointedWal_ObservingReader_NeverSeesForeignPairing()
    {
        // Production RestoreCheckpointAsync over a live file whose committed
        // data exists only in the WAL: a pinned reader positioned during the
        // replacement must observe the old committed boundary (never a foreign
        // main+WAL pairing), and every post-restore read must see the
        // intended checkpoint. The pinned reader deliberately holds an
        // unpooled handle across the replacement, so this test assumes POSIX
        // rename/unlink semantics (the CI platform).
        var (store, root) = CreateStore();
        SemaphoreSlim? snapshotGate = null;
        bool snapshotHeld = false;
        try
        {
            CraftedSave save = await CreateCraftedSaveAsync(store, root, "Boundary", 9611UL, 9612UL);

            // Freeze the restore inside its pre-restore-safety checkpoint so
            // the observing reader is positioned at the replacement boundary.
            snapshotGate = AcquirePrivateGate("SnapshotGates", save.SaveId);
            await snapshotGate.WaitAsync();
            snapshotHeld = true;
            Task<SaveStore.SaveDetailRecord> restoreTask = store.RestoreCheckpointAsync(save.SaveId, save.Checkpoint.CheckpointId);
            await WaitUntilMigrationHeldAsync(save.SaveId, restoreTask);

            using SqliteConnection observer = await OpenPinnedReaderAsync(save.LivePath);
            string observed = (string)(await ExecuteScalarAsync(observer, "SELECT Name FROM SaveMetadata WHERE Id = 1;"))!;

            snapshotGate.Release();
            snapshotHeld = false;
            SaveStore.SaveDetailRecord restored = await restoreTask;
            await observer.DisposeAsync();

            await AssertRestoredBoundaryAsync(store, save, observed, restored);
        }
        finally
        {
            if (snapshotHeld && snapshotGate is not null)
            {
                try
                {
                    snapshotGate.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }

            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RestoreInterruptedMidReplacement_LeavesNoStaleCompanionsBesideLiveMain()
    {
        // Crash-between-steps probe through the production restore: the
        // replacement is interrupted at its move-in copy (the already-verified
        // checkpoint file becomes unreadable while the restore is frozen at
        // its pre-restore-safety checkpoint). The invariant under test: by
        // the time the replacement attempts to move the new main in, the
        // discarded live file's companions are already gone, so an
        // interruption can never leave a stale WAL that a later open pairs
        // with the live main. On the pre-change ordering (companions deleted
        // only after the move) the interrupted restore leaves the stale WAL
        // at the path and this test fails. The pinned reader keeps the
        // crafted WAL uncheckpointed; the flow assumes POSIX semantics (the
        // CI platform).
        var (store, root) = CreateStore();
        try
        {
            CraftedSave save = await CreateCraftedSaveAsync(store, root, "Boundary", 9621UL, 9622UL);
            using SqliteConnection observer = await OpenPinnedReaderAsync(save.LivePath);

            await InterruptRestoreAtReplacementCopyAsync(store, save);

            File.Exists(save.LivePath + "-wal").ShouldBeFalse();
            File.Exists(save.LivePath + "-shm").ShouldBeFalse();

            // Closing the pinned reader folds its committed boundary back
            // into the unchanged live main: the failed restore correctly leaves
            // the save at its pre-restore committed state, with no WAL beside
            // the main file for any later open to pair with.
            await observer.DisposeAsync();
            (await ReadLiveNameAsync(save.LivePath)).ShouldBe("Stale");

            // The interrupted restore leaves a healthy live file: a clean
            // retry still lands the intended checkpoint.
            SaveStore.SaveDetailRecord recovered = await store.RestoreCheckpointAsync(save.SaveId, save.Checkpoint.CheckpointId);
            recovered.ShouldBe(save.Created.Detail);

            SqliteConnection.ClearAllPools();
            File.Exists(save.LivePath + "-wal").ShouldBeFalse();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    /// <summary>
    /// Creates a fresh save, a verified checkpoint of its initial state, and
    /// the crafted crashed-writer live state: main file restored to the
    /// checkpoint bytes plus a WAL holding one committed-but-uncheckpointed
    /// metadata update ("Stale").
    /// </summary>
    private static async Task<CraftedSave> CreateCraftedSaveAsync(
        SaveStore store,
        string root,
        string name,
        ulong seed,
        ulong stream)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(name, seed, stream, UniverseTestCatalog.Build()).ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        string livePath = store.GetSaveFilePath(saveId);
        SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(saveId, "pre-failure").ConfigureAwait(false);
        string checkpointPath = SaveCheckpointFiles.GetCheckpointDatabasePath(
            SaveCheckpointFiles.GetSaveCheckpointDirectory(root, saveId), checkpoint.CheckpointId);
        await CraftCommittedUncheckpointedWalAsync(livePath, checkpointPath, "Stale").ConfigureAwait(false);
        return new CraftedSave(created, saveId, livePath, checkpoint, checkpointPath);
    }

    private static async Task AssertRestoredBoundaryAsync(
        SaveStore store,
        CraftedSave save,
        string observed,
        SaveStore.SaveDetailRecord restored)
    {
        // Mid-restore reader: the crafted main+WAL were a matched pair, so the
        // observation is the old committed content, never a foreign pairing.
        observed.ShouldBe("Stale");
        restored.ShouldBe(save.Created.Detail);
        (await ReadLiveNameAsync(save.LivePath).ConfigureAwait(false)).ShouldBe("Boundary");
        SaveStore.SaveDetailRecord reopened = await store.OpenAsync(save.SaveId).ConfigureAwait(false);
        reopened.ShouldBe(save.Created.Detail);

        // The discarded committed state was preserved by the pre-restore-safety
        // checkpoint, and no stale WAL survives the replaced main file.
        IReadOnlyList<SaveStore.CheckpointRecord> listed = await store
            .ListCheckpointsAsync(save.SaveId)
            .ConfigureAwait(false);
        listed.Any(e => string.Equals(e.Reason, "pre-restore-safety", StringComparison.Ordinal)
            && string.Equals(e.SaveName, "Stale", StringComparison.Ordinal)).ShouldBeTrue();

        SqliteConnection.ClearAllPools();
        File.Exists(save.LivePath + "-wal").ShouldBeFalse();
    }

    /// <summary>
    /// Opens an unpooled connection with an active deferred read transaction,
    /// pinning the WAL like MSS-126's readers: the committed-but-uncheckpointed
    /// frames survive other connections' clean closes.
    /// </summary>
    private static async Task<SqliteConnection> OpenPinnedReaderAsync(string livePath)
    {
        SqliteConnection reader = new(
            new SqliteConnectionStringBuilder { DataSource = livePath, Pooling = false }.ToString());
        await reader.OpenAsync().ConfigureAwait(false);
        await ExecuteAsync(reader, "BEGIN DEFERRED;").ConfigureAwait(false);
        _ = await ExecuteScalarAsync(reader, "SELECT Name FROM SaveMetadata WHERE Id = 1;").ConfigureAwait(false);
        return reader;
    }

    /// <summary>
    /// Executes one replacement ordering against the crafted live state and
    /// returns what a reader opening the live path observes at the window: the
    /// new main has just moved in and the companions were either already
    /// deleted (enforced ordering) or are deleted only after the reader
    /// (pre-change ordering).
    /// </summary>
    private static async Task<string> ReplaceMainInWindowAndReadAsync(
        string livePath,
        string checkpointDatabasePath,
        string stagingPath,
        bool deleteCompanionsFirst)
    {
        await CraftCommittedUncheckpointedWalAsync(livePath, checkpointDatabasePath, "Stale").ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        if (deleteCompanionsFirst)
        {
            DeleteCompanionFiles(livePath);
        }

        File.Copy(checkpointDatabasePath, stagingPath);
        File.Move(stagingPath, livePath, overwrite: true);

        string observed = await ReadLiveNameAsync(livePath).ConfigureAwait(false);
        if (!deleteCompanionsFirst)
        {
            DeleteCompanionFiles(livePath);
        }

        return observed;
    }

    /// <summary>
    /// Crafts a crashed-writer state: the live main database is reset to the
    /// checkpoint bytes, a pinned read transaction plus one committed update
    /// leave the new name only in the WAL, and the committed-but-uncheckpointed
    /// main+WAL pair is then written back with no open handles. The WAL frames
    /// target the same page layout as the bytes the replacement later moves
    /// in, so a foreign pairing replays them deterministically.
    /// </summary>
    private static async Task CraftCommittedUncheckpointedWalAsync(
        string livePath,
        string checkpointDatabasePath,
        string staleName)
    {
        SqliteConnection.ClearAllPools();
        DeleteCompanionFiles(livePath);
        File.Copy(checkpointDatabasePath, livePath, overwrite: true);

        byte[] mainBytes;
        byte[] walBytes;
        using (SqliteConnection reader = new(
            new SqliteConnectionStringBuilder { DataSource = livePath, Pooling = false }.ToString()))
        {
            await reader.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(reader, "BEGIN DEFERRED;").ConfigureAwait(false);
            _ = await ExecuteScalarAsync(reader, "SELECT Name FROM SaveMetadata WHERE Id = 1;").ConfigureAwait(false);

            using SqliteConnection writer = new(
                new SqliteConnectionStringBuilder { DataSource = livePath, Pooling = false }.ToString());
            await writer.OpenAsync().ConfigureAwait(false);
            await ExecuteAsync(writer, "PRAGMA busy_timeout=5000;").ConfigureAwait(false);
            await ExecuteAsync(writer, $"UPDATE SaveMetadata SET Name = '{staleName}' WHERE Id = 1;").ConfigureAwait(false);

            mainBytes = await File.ReadAllBytesAsync(livePath).ConfigureAwait(false);
            walBytes = await File.ReadAllBytesAsync(livePath + "-wal").ConfigureAwait(false);
        }

        // The clean close folds the frames into the main and removes the WAL;
        // writing the captured bytes back simulates the writer crash.
        await File.WriteAllBytesAsync(livePath, mainBytes).ConfigureAwait(false);
        await File.WriteAllBytesAsync(livePath + "-wal", walBytes).ConfigureAwait(false);
        if (File.Exists(livePath + "-shm"))
        {
            File.Delete(livePath + "-shm");
        }

        SqliteConnection.ClearAllPools();
    }

    private static async Task<string> ReadLiveNameAsync(string livePath)
    {
        using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder { DataSource = livePath, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        return (string)(await ExecuteScalarAsync(connection, "SELECT Name FROM SaveMetadata WHERE Id = 1;").ConfigureAwait(false))!;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<object?> ExecuteScalarAsync(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Runs RestoreCheckpointAsync until it is frozen inside its
    /// pre-restore-safety checkpoint (the verified checkpoint was already
    /// read at that point), makes the checkpoint file unreadable, and lets
    /// the replacement fault at its copy — the deterministic interruption
    /// point after companion handling and before the new main database can
    /// move in. Restores the checkpoint file mode afterwards. Requires POSIX
    /// file semantics (the Linux CI platform).
    /// </summary>
    private static async Task InterruptRestoreAtReplacementCopyAsync(SaveStore store, CraftedSave save)
    {
        SemaphoreSlim snapshotGate = AcquirePrivateGate("SnapshotGates", save.SaveId);
        await snapshotGate.WaitAsync().ConfigureAwait(false);
        bool held = true;
        try
        {
            Task<SaveStore.SaveDetailRecord> restoreTask = store.RestoreCheckpointAsync(save.SaveId, save.Checkpoint.CheckpointId);
            await WaitUntilMigrationHeldAsync(save.SaveId, restoreTask).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(save.CheckpointPath, UnixFileMode.None);
            }
            else
            {
                throw new Xunit.Sdk.XunitException("The interrupted-restore probe requires POSIX file semantics (Linux CI).");
            }

            snapshotGate.Release();
            held = false;
            await Should.ThrowAsync<UnauthorizedAccessException>(() => restoreTask).ConfigureAwait(false);
        }
        finally
        {
            if (held)
            {
                snapshotGate.Release();
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(save.CheckpointPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private static void DeleteCompanionFiles(string saveFilePath)
    {
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            string companion = saveFilePath + suffix;
            if (File.Exists(companion))
            {
                File.Delete(companion);
            }
        }
    }

    private static SemaphoreSlim AcquirePrivateGate(string fieldName, Guid saveId)
    {
        FieldInfo? field = typeof(SaveStore).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        field.ShouldNotBeNull($"SaveStore.{fieldName} gate map must exist.");
        ConcurrentDictionary<Guid, SemaphoreSlim> map =
            (ConcurrentDictionary<Guid, SemaphoreSlim>)field.GetValue(null)!;
        return map.GetOrAdd(saveId, static _ => new SemaphoreSlim(1, 1));
    }

    private static async Task WaitUntilMigrationHeldAsync(Guid saveId, Task blocked)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (AcquirePrivateGate("MigrationGates", saveId).CurrentCount != 0 || blocked.IsCompleted)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new Xunit.Sdk.XunitException("RestoreCheckpointAsync did not acquire the migration gate.");
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-mss148-" + Guid.NewGuid().ToString("N"));
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
