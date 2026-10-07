using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Tests.Features;

/// <summary>
/// Shared construction and file-copy forking for file-per-save SQLite test saves.
/// MSS-067: most integration tests repeat an identical expensive setup twice
/// (usually a full Season 1 simulation) to compare two execution paths from the
/// same starting state. Building the same seeded universe twice doubles the
/// setup cost while proving nothing extra: the same seed already determines the
/// same sporting content. <see cref="ForkAsync"/> builds once and clones the
/// committed database file, so both sides still start from bit-identical
/// sporting state (same universe, leagues, standings, RNG) in fully isolated
/// temp roots.
/// Isolation: each fork lives in its own temp root with its own
/// <see cref="SaveStore"/>; no mutable sporting state is shared. The source
/// save is checkpointed (WAL frames forced into the main file, pools cleared)
/// before the copy, mirroring the production checkpoint path, so the clone is
/// a consistent committed snapshot. Concurrent forks only read the source file.
/// </summary>
internal static class TestSaveStores
{
    internal static (SaveStore Store, string Root) CreateStore(string prefix)
    {
        string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (CreateStoreForRoot(root), root);
    }

    internal static SaveStore CreateStoreForRoot(string root)
    {
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        return new SaveStore(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
    }

    /// <summary>
    /// Clones a prepared save into a new isolated temp root under the same
    /// save id (identity is the file name inside its own root, so sharing the
    /// id across roots is safe). The source is left untouched.
    /// </summary>
    internal static async Task<(SaveStore Store, string Root, Guid SaveId)> ForkAsync(
        SaveStore sourceStore,
        Guid sourceSaveId,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(sourceStore);
        if (sourceSaveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(sourceSaveId));
        }

        string sourcePath = sourceStore.GetSaveFilePath(sourceSaveId);
        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException($"Cannot fork missing save file '{sourcePath}'.");
        }

        await CheckpointAsync(sourceStore, sourceSaveId).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();

        string root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string destinationPath = SaveFileNaming.GetSaveFilePath(root, sourceSaveId);
        File.Copy(sourcePath, destinationPath);

        return (CreateStoreForRoot(root), root, sourceSaveId);
    }

    internal static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
    }

    private static async Task CheckpointAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false);
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
