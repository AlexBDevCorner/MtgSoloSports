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
/// Store-level integration tests for the technical recovery/checkpoint
/// mechanism: verified creation, boundary restore after failure, tamper
/// rejection, schema/rules separation on migration, and retention.
/// </summary>
public sealed class SaveCheckpointTests
{
    [Fact]
    public async Task CreateCheckpoint_IsVerified_AndListable()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Checkpointed", 41UL, 42UL, UniverseTestCatalog.Build());
            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-test");

            checkpoint.SaveId.ShouldBe(created.Detail.SaveId);
            checkpoint.CheckpointId.ShouldNotBe(Guid.Empty);
            checkpoint.Reason.ShouldBe("pre-test");
            checkpoint.SaveName.ShouldBe("Checkpointed");
            checkpoint.SchemaVersion.ShouldBe(SaveSchemaVersion.Current);
            checkpoint.CurrentSeason.ShouldBe(1);
            checkpoint.Phase.ShouldBe("SeasonInProgress");
            checkpoint.DatabaseSha256.Length.ShouldBe(64);

            IReadOnlyList<SaveStore.CheckpointRecord> listed = await store.ListCheckpointsAsync(created.Detail.SaveId);
            listed.Count.ShouldBe(1);
            listed[0].ShouldBe(checkpoint);

            string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, created.Detail.SaveId);
            File.Exists(SaveCheckpointFiles.GetCheckpointDatabasePath(directory, checkpoint.CheckpointId)).ShouldBeTrue();
            File.Exists(SaveCheckpointFiles.GetCheckpointSidecarPath(directory, checkpoint.CheckpointId)).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreCheckpoint_RestoresLastKnownBoundary()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Boundary", 43UL, 44UL, UniverseTestCatalog.Build());
            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-failure");
            await CorruptLiveSaveAsync(store, created.Detail.SaveId);

            SaveStore.SaveDetailRecord corrupted = await store.OpenAsync(created.Detail.SaveId);
            corrupted.Name.ShouldBe("Corrupted");
            corrupted.RngState.ShouldNotBe(created.Detail.RngState);

            SaveStore.SaveDetailRecord restored = await store.RestoreCheckpointAsync(created.Detail.SaveId, checkpoint.CheckpointId);
            restored.ShouldBe(created.Detail);

            IReadOnlyList<SaveStore.CheckpointRecord> listed = await store.ListCheckpointsAsync(created.Detail.SaveId);
            listed.Count.ShouldBe(2);
            listed.Any(e => string.Equals(e.Reason, "pre-restore-safety", StringComparison.Ordinal)).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_UnknownCheckpoint_ThrowsNotFound_LiveUntouched()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Steady", 45UL, 46UL, UniverseTestCatalog.Build());
            await Should.ThrowAsync<SaveCheckpointNotFoundException>(
                () => store.RestoreCheckpointAsync(created.Detail.SaveId, Guid.NewGuid()));

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.Detail.SaveId);
            reopened.ShouldBe(created.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_TamperedCheckpoint_IsRejected_LiveUntouched()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Guarded", 47UL, 48UL, UniverseTestCatalog.Build());
            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-tamper");

            string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, created.Detail.SaveId);
            string databasePath = SaveCheckpointFiles.GetCheckpointDatabasePath(directory, checkpoint.CheckpointId);
            byte[] bytes = await File.ReadAllBytesAsync(databasePath);
            bytes[200] ^= 0xFF;
            await File.WriteAllBytesAsync(databasePath, bytes);

            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
                () => store.RestoreCheckpointAsync(created.Detail.SaveId, checkpoint.CheckpointId));
            failure.Message.ShouldContain("checksum");

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.Detail.SaveId);
            reopened.ShouldBe(created.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Migration_WithNoPendingMigrations_LeavesRulesAndRngUnchanged()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Stable", 49UL, 50UL, UniverseTestCatalog.Build());
            string rulesBefore = created.Detail.RulesJson;

            await store.EnsureMigratedAsync(created.Detail.SaveId);

            SaveStore.SaveDetailRecord after = await store.OpenAsync(created.Detail.SaveId);
            after.RulesJson.ShouldBe(rulesBefore);
            after.RngState.ShouldBe(created.Detail.RngState);
            after.RngStream.ShouldBe(created.Detail.RngStream);
            (await store.ListCheckpointsAsync(created.Detail.SaveId)).Count.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteSave_RemovesCheckpoints()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Doomed", 51UL, 52UL, UniverseTestCatalog.Build());
            _ = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-delete");
            string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, created.Detail.SaveId);
            Directory.Exists(directory).ShouldBeTrue();

            await store.DeleteAsync(created.Detail.SaveId);

            Directory.Exists(directory).ShouldBeFalse();
            (await store.ListCheckpointsAsync(created.Detail.SaveId)).Count.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateCheckpoint_BeyondRetention_PruesOldest()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Retained", 53UL, 54UL, UniverseTestCatalog.Build());
            for (int i = 0; i < SaveCheckpointFiles.MaxCheckpointsPerSave + 2; i++)
            {
                _ = await store.CreateCheckpointAsync(created.Detail.SaveId, $"checkpoint-{i}");
            }

            IReadOnlyList<SaveStore.CheckpointRecord> listed = await store.ListCheckpointsAsync(created.Detail.SaveId);
            listed.Count.ShouldBe(SaveCheckpointFiles.MaxCheckpointsPerSave);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreCheckpoint_RestoresWhenLiveRngRowIsMissing()
    {
        // MSS-127: deleting the live RNG row must not block recovery from a
        // verified checkpoint. Fresh save only; no simulated seasons.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("RngDamaged", 61UL, 62UL, UniverseTestCatalog.Build());
            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-damage");

            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                _ = await context.Database.ExecuteSqlRawAsync("DELETE FROM \"RngStates\";");
            }

            SqliteConnection.ClearAllPools();
            await Should.ThrowAsync<InvalidOperationException>(() => store.OpenAsync(created.Detail.SaveId));

            SaveStore.SaveDetailRecord restored = await store.RestoreCheckpointAsync(created.Detail.SaveId, checkpoint.CheckpointId);
            restored.ShouldBe(created.Detail);

            string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, created.Detail.SaveId);
            File.Exists(SaveCheckpointFiles.GetDamagedLiveBackupPath(directory, checkpoint.CheckpointId)).ShouldBeTrue();

            IReadOnlyList<SaveStore.CheckpointRecord> listed = await store.ListCheckpointsAsync(created.Detail.SaveId);
            listed.Count.ShouldBe(1);
            listed.Any(e => string.Equals(e.Reason, "pre-restore-safety", StringComparison.Ordinal)).ShouldBeFalse();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreCheckpoint_RestoresWhenLiveRulesAreMalformed()
    {
        // MSS-127: malformed live rules must not block recovery. Fresh save only.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("RulesDamaged", 63UL, 64UL, UniverseTestCatalog.Build());
            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-damage");

            using (SaveDbContext context = store.OpenDbContext(created.Detail.SaveId))
            {
                RulesSnapshotEntity rules = await context.RulesSnapshots.SingleAsync(e => e.Id == 1);
                rules.RulesJson = "not-json";
                _ = await context.SaveChangesAsync();
            }

            SqliteConnection.ClearAllPools();
            await Should.ThrowAsync<InvalidOperationException>(() => store.OpenAsync(created.Detail.SaveId));

            SaveStore.SaveDetailRecord restored = await store.RestoreCheckpointAsync(created.Detail.SaveId, checkpoint.CheckpointId);
            restored.ShouldBe(created.Detail);

            string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, created.Detail.SaveId);
            File.Exists(SaveCheckpointFiles.GetDamagedLiveBackupPath(directory, checkpoint.CheckpointId)).ShouldBeTrue();

            IReadOnlyList<SaveStore.CheckpointRecord> listed = await store.ListCheckpointsAsync(created.Detail.SaveId);
            listed.Count.ShouldBe(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreCheckpoint_RestoresWhenLiveFileIsMissing()
    {
        // MSS-127: a lost live file must not block recovery when the
        // checkpoint directory still holds a verified backup. Fresh save only.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("FileLost", 65UL, 66UL, UniverseTestCatalog.Build());
            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-loss");

            DeleteLiveFile(store.GetSaveFilePath(created.Detail.SaveId));

            SaveStore.SaveDetailRecord restored = await store.RestoreCheckpointAsync(created.Detail.SaveId, checkpoint.CheckpointId);
            restored.ShouldBe(created.Detail);

            string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, created.Detail.SaveId);
            File.Exists(SaveCheckpointFiles.GetDamagedLiveBackupPath(directory, checkpoint.CheckpointId)).ShouldBeFalse();

            IReadOnlyList<SaveStore.CheckpointRecord> listed = await store.ListCheckpointsAsync(created.Detail.SaveId);
            listed.Count.ShouldBe(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreCheckpoint_RestoresWhenLiveDatabaseIsUnreadable()
    {
        // MSS-127: an unreadable live database file must not block recovery.
        // Fresh save only; no simulated seasons.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Unreadable", 67UL, 68UL, UniverseTestCatalog.Build());
            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-damage");

            string livePath = store.GetSaveFilePath(created.Detail.SaveId);
            SqliteConnection.ClearAllPools();
            await File.WriteAllBytesAsync(livePath, "this is not a sqlite database"u8.ToArray());
            DeleteCompanionFiles(livePath);
            SqliteConnection.ClearAllPools();

            SaveStore.SaveDetailRecord restored = await store.RestoreCheckpointAsync(created.Detail.SaveId, checkpoint.CheckpointId);
            restored.ShouldBe(created.Detail);

            string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, created.Detail.SaveId);
            File.Exists(SaveCheckpointFiles.GetDamagedLiveBackupPath(directory, checkpoint.CheckpointId)).ShouldBeTrue();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreCheckpoint_CorruptContent_IsRejected_LiveUntouched()
    {
        // MSS-127: a checkpoint whose bytes no longer carry the required rows
        // is rejected even when its sidecar checksum was refreshed to match,
        // and the live save is left untouched. Fresh save only.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("GuardedContent", 69UL, 70UL, UniverseTestCatalog.Build());
            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-tamper");

            string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, created.Detail.SaveId);
            string databasePath = SaveCheckpointFiles.GetCheckpointDatabasePath(directory, checkpoint.CheckpointId);
            await ExecuteCheckpointSqlAsync(databasePath, "DELETE FROM \"RngStates\";");
            await RefreshCheckpointSidecarShaAsync(directory, checkpoint.CheckpointId);
            SqliteConnection.ClearAllPools();

            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
                () => store.RestoreCheckpointAsync(created.Detail.SaveId, checkpoint.CheckpointId));
            failure.Message.ShouldContain("missing required rows");

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.Detail.SaveId);
            reopened.ShouldBe(created.Detail);
            File.Exists(SaveCheckpointFiles.GetDamagedLiveBackupPath(directory, checkpoint.CheckpointId)).ShouldBeFalse();
            (await store.ListCheckpointsAsync(created.Detail.SaveId)).Count.ShouldBe(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreCheckpoint_IdentityMismatch_IsRejected_LiveUntouched()
    {
        // MSS-127: a checkpoint whose embedded identity no longer matches the
        // save is rejected even with a refreshed checksum. Fresh save only.
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("GuardedIdentity", 71UL, 72UL, UniverseTestCatalog.Build());
            SaveStore.CheckpointRecord checkpoint = await store.CreateCheckpointAsync(created.Detail.SaveId, "pre-tamper");

            string directory = SaveCheckpointFiles.GetSaveCheckpointDirectory(root, created.Detail.SaveId);
            string databasePath = SaveCheckpointFiles.GetCheckpointDatabasePath(directory, checkpoint.CheckpointId);
            Guid impostor = Guid.NewGuid();
            await ExecuteCheckpointSqlAsync(databasePath, $"UPDATE \"SaveMetadata\" SET \"SaveId\" = '{impostor:D}' WHERE \"Id\" = 1;");
            await RefreshCheckpointSidecarShaAsync(directory, checkpoint.CheckpointId);
            SqliteConnection.ClearAllPools();

            InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
                () => store.RestoreCheckpointAsync(created.Detail.SaveId, checkpoint.CheckpointId));
            failure.Message.ShouldContain("mismatched identity");

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.Detail.SaveId);
            reopened.ShouldBe(created.Detail);
            File.Exists(SaveCheckpointFiles.GetDamagedLiveBackupPath(directory, checkpoint.CheckpointId)).ShouldBeFalse();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CorruptLiveSaveAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SaveMetadataEntity metadata = await context.SaveMetadata.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        metadata.Name = "Corrupted";
        metadata.CurrentSeason = 99;
        RngStateEntity rng = await context.RngStates.SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        rng.State = 12345L;
        rng.Stream = 67890L;
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static void DeleteLiveFile(string livePath)
    {
        SqliteConnection.ClearAllPools();
        DeleteCompanionFiles(livePath);
        if (File.Exists(livePath))
        {
            File.Delete(livePath);
        }

        SqliteConnection.ClearAllPools();
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

    private static async Task ExecuteCheckpointSqlAsync(string checkpointDatabasePath, string sql)
    {
        SqliteConnection.ClearAllPools();
        using SqliteConnection connection = new($"Data Source={checkpointDatabasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using (SqliteCommand checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            _ = await checkpoint.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private static async Task RefreshCheckpointSidecarShaAsync(string directory, Guid checkpointId)
    {
        string databasePath = SaveCheckpointFiles.GetCheckpointDatabasePath(directory, checkpointId);
        string sidecarPath = SaveCheckpointFiles.GetCheckpointSidecarPath(directory, checkpointId);
        SaveCheckpointSidecar sidecar = SaveCheckpointSidecar.FromJson(await File.ReadAllTextAsync(sidecarPath).ConfigureAwait(false));
        string tamperedSha = SaveBundle.ComputeFileSha256Hex(databasePath);
        await File.WriteAllTextAsync(sidecarPath, (sidecar with { DatabaseSha256 = tamperedSha }).ToJson()).ConfigureAwait(false);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-checkpoint-" + Guid.NewGuid().ToString("N"));
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
