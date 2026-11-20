using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.GetCatalogStats;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.SimulationKernel.Catalog;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Catalog;

/// <summary>
/// MSS-147 catalog schema-migration coverage: catalogs created by the
/// previous version (plain EnsureCreated schema, no history table) keep
/// working without data loss, fresh installs are created once through EF
/// Core migrations, the first read against an existing file adopts it once
/// (baselining or pending migrations) and later reads issue no schema work,
/// connections run the documented pragmas, and incompatible future catalogs
/// fail loudly with re-import guidance from imports and reads alike instead
/// of a runtime "no such column" break.
/// </summary>
public sealed class CatalogSchemaMigrationTests
{
    [Fact]
    public async Task LegacyCatalog_FromPreviousVersion_StillServesReadsAndImports()
    {
        var (store, factory, root) = CreateStore();
        try
        {
            string path = store.GetCatalogFilePath();
            await CreateLegacyCatalogAsync(factory, path, [Athlete("Legacy White One", SportingColor.White)]);

            // The first read adopts the legacy file into the migrations story
            // (baseline + migrate, no data loss); every later read is plain.
            IReadOnlyList<CatalogAthlete> athletes = await store.ListAthletesAsync();
            athletes.Count.ShouldBe(1);
            (await store.GetTotalAsync()).ShouldBe(1);
            (await store.GetCountsAsync())[SportingColor.White].ShouldBe(1);

            // Import round-trip against the legacy catalog: baselining + atomic replace, no data loss in between.
            ImportCatalogHandler handler = new(store);
            ImportCatalogResponse imported = await handler.HandleAsync(new ImportCatalogRequest(BlackCardJson("Black Legacy Two")));
            imported.UniqueAthletes.ShouldBe(1);
            (await store.GetTotalAsync()).ShouldBe(1);
            IReadOnlyDictionary<SportingColor, int> counts = await store.GetCountsAsync();
            counts[SportingColor.Black].ShouldBe(1);
            counts[SportingColor.White].ShouldBe(0);

            // The legacy file was adopted into the migrations story (baselined, not recreated) and stays in WAL.
            (await ReadHistoryIdsAsync(path)).ShouldBe([CatalogSchemaMigrator.InitialMigrationId]);
            (await ReadJournalModeAsync(path)).ShouldBe("wal");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MigratedCatalog_CreatesTheSameShapeAsThePreviousVersion()
    {
        var (store, factory, root) = CreateStore();
        try
        {
            // The pre-migration catalog shape is what EnsureCreated produced on every call.
            string legacyPath = Path.Combine(root, "legacy.db");
            using (CatalogDbContext context = factory.Create(legacyPath))
            {
                _ = await context.Database.EnsureCreatedAsync();
            }

            string migratedPath = Path.Combine(root, "migrated.db");
            await CatalogSchemaMigrator.EnsureSchemaAsync(factory, migratedPath);

            // Except for EF Core migration bookkeeping (history and lock tables,
            // only the migrated file may have them), both files must expose
            // identical schema objects with identical DDL.
            (await ReadSchemaObjectsAsync(legacyPath)).ShouldBe(await ReadSchemaObjectsAsync(migratedPath));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportIntoMissingCatalog_CreatesMigratedSchemaOnce()
    {
        var (store, factory, root) = CreateStore();
        try
        {
            string path = store.GetCatalogFilePath();
            File.Exists(path).ShouldBeFalse();

            // Reads on a fresh install report an empty catalog and create no file.
            GetCatalogStatsHandler stats = new(store);
            _ = await stats.HandleAsync();
            File.Exists(path).ShouldBeFalse();

            ImportCatalogHandler handler = new(store);
            ImportCatalogResponse imported = await handler.HandleAsync(new ImportCatalogRequest(WhiteCardJson("White One")));
            imported.UniqueAthletes.ShouldBe(1);
            (await ReadHistoryIdsAsync(path)).ShouldBe([CatalogSchemaMigrator.InitialMigrationId]);

            // A second import reuses the migrated schema instead of re-initializing it.
            ImportCatalogResponse again = await handler.HandleAsync(new ImportCatalogRequest(BlackCardJson("Black One")));
            again.UniqueAthletes.ShouldBe(1);
            (await store.GetTotalAsync()).ShouldBe(1);
            (await ReadHistoryIdsAsync(path)).ShouldBe([CatalogSchemaMigrator.InitialMigrationId]);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task NormalRead_IssuesNoSchemaStatements()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-mss147-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RecordingCatalogInterceptor recorder = new();
        IOptions<CatalogStorageOptions> options = Options.Create(new CatalogStorageOptions { CatalogPath = Path.Combine(root, "catalog.db") });
        CatalogDbContextFactory factory = new(recorder);
        CatalogStore store = new(options, new TestHostEnvironment(root), factory);
        try
        {
            ImportCatalogHandler handler = new(store);
            _ = await handler.HandleAsync(new ImportCatalogRequest(WhiteCardJson("White One")));
            recorder.Commands.Clear();

            GetCatalogStatsHandler stats = new(store);
            _ = await stats.HandleAsync();
            IReadOnlyList<CatalogAthlete> athletes = await store.ListAthletesAsync();

            athletes.Count.ShouldBe(1);
            recorder.Commands.ShouldNotBeEmpty();
            // Every command on the read path is a SELECT: no CREATE/ALTER/DROP,
            // no sqlite_master schema check, no __EFMigrationsHistory round-trip.
            // (Connection pragmas run on the raw connection, below EF's command pipeline.)
            recorder.Commands.ShouldAllBe(static command =>
                command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task LegacyCatalog_ReadPath_AdoptsSchemaOnceThenReadsPlain()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-mss147-adopt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RecordingCatalogInterceptor recorder = new();
        IOptions<CatalogStorageOptions> options = Options.Create(new CatalogStorageOptions { CatalogPath = Path.Combine(root, "catalog.db") });
        CatalogDbContextFactory factory = new(recorder);
        CatalogStore store = new(options, new TestHostEnvironment(root), factory);
        try
        {
            string path = store.GetCatalogFilePath();
            await CreateLegacyCatalogAsync(factory, path, [Athlete("Legacy White One", SportingColor.White)]);
            recorder.Commands.Clear();

            // MSS-147 correction: a read (not an import) adopts the legacy file
            // into the migrations story — baseline, migrate, WAL — and serves it,
            // so save creation never hits a raw legacy-shape break.
            GetCatalogStatsHandler stats = new(store);
            _ = await stats.HandleAsync();
            (await store.ListAthletesAsync()).Count.ShouldBe(1);
            (await ReadHistoryIdsAsync(path)).ShouldBe([CatalogSchemaMigrator.InitialMigrationId]);
            (await ReadJournalModeAsync(path)).ShouldBe("wal");

            // The schema pass ran exactly once per process: later reads are
            // plain SELECTs only, with no DDL or history round-trip.
            recorder.Commands.Clear();
            _ = await stats.HandleAsync();
            IReadOnlyList<CatalogAthlete> athletes = await store.ListAthletesAsync();
            athletes.Count.ShouldBe(1);
            recorder.Commands.ShouldNotBeEmpty();
            recorder.Commands.ShouldAllBe(static command =>
                command.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DamagedLegacyCatalog_ReadPath_FailsLoudlyWithReImportGuidance()
    {
        var (store, factory, root) = CreateStore();
        try
        {
            string path = store.GetCatalogFilePath();
            await CreateLegacyCatalogAsync(factory, path, [Athlete("Damaged One", SportingColor.White)]);
            using (SqliteConnection connection = await OpenRawAsync(path))
            using (DbCommand alter = connection.CreateCommand())
            {
                // A column the pre-migration schema never had: damaged or future-shaped.
                alter.CommandText = "ALTER TABLE \"CatalogAthletes\" ADD COLUMN \"FutureColumn\" TEXT;";
                _ = await alter.ExecuteNonQueryAsync();
            }

            // MSS-147 correction: the read path reaches the loud guard with
            // actionable guidance, never the raw "no such column" break.
            CatalogSchemaIncompatibleException ex = await Should.ThrowAsync<CatalogSchemaIncompatibleException>(
                () => store.GetTotalAsync());
            ex.Message.ShouldContain("does not match the pre-migration catalog schema");
            ex.Message.ShouldContain("The catalog is fully re-importable");

            // The failed verification is never cached: reads stay loud until the
            // file is fixed, and the rows survive untouched.
            _ = await Should.ThrowAsync<CatalogSchemaIncompatibleException>(() => store.ListAthletesAsync());
            (await ReadAthleteCountAsync(path)).ShouldBe(1);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FutureCatalog_WithUnknownMigration_FailsLoudlyAndKeepsData()
    {
        var (store, factory, root) = CreateStore();
        try
        {
            string path = store.GetCatalogFilePath();
            await CatalogSchemaMigrator.EnsureSchemaAsync(factory, path);
            const string FutureMigrationId = "29991231235959_FromTheFuture";
            await InsertHistoryRowAsync(path, FutureMigrationId);

            ImportCatalogHandler handler = new(store);
            CatalogSchemaIncompatibleException ex = await Should.ThrowAsync<CatalogSchemaIncompatibleException>(
                () => handler.HandleAsync(new ImportCatalogRequest(WhiteCardJson("White One"))));
            ex.Message.ShouldContain("newer application version");
            ex.Message.ShouldContain(FutureMigrationId);
            ex.Message.ShouldContain("The catalog is fully re-importable");

            // The failure aborted before any mutation, and the read path reaches
            // the same loud guard (never a raw "no such column" break).
            CatalogSchemaIncompatibleException readEx = await Should.ThrowAsync<CatalogSchemaIncompatibleException>(
                () => store.GetTotalAsync());
            readEx.Message.ShouldContain("newer application version");
            (await ReadAthleteCountAsync(path)).ShouldBe(0);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DamagedLegacyCatalog_FailsLoudlyInsteadOfBaseliningAndKeepsData()
    {
        var (store, factory, root) = CreateStore();
        try
        {
            string path = store.GetCatalogFilePath();
            await CreateLegacyCatalogAsync(factory, path, [Athlete("Damaged One", SportingColor.White)]);
            using (SqliteConnection connection = await OpenRawAsync(path))
            using (DbCommand alter = connection.CreateCommand())
            {
                // A column the pre-migration schema never had: damaged or future-shaped.
                alter.CommandText = "ALTER TABLE \"CatalogAthletes\" ADD COLUMN \"FutureColumn\" TEXT;";
                _ = await alter.ExecuteNonQueryAsync();
            }

            ImportCatalogHandler handler = new(store);
            CatalogSchemaIncompatibleException ex = await Should.ThrowAsync<CatalogSchemaIncompatibleException>(
                () => handler.HandleAsync(new ImportCatalogRequest(WhiteCardJson("White One"))));
            ex.Message.ShouldContain("does not match the pre-migration catalog schema");
            ex.Message.ShouldContain("FutureColumn");
            ex.Message.ShouldContain("The catalog is fully re-importable");

            // No silent repair and no destruction: the rows are untouched, and
            // the read path stays loud with guidance instead of failing raw.
            _ = await Should.ThrowAsync<CatalogSchemaIncompatibleException>(() => store.GetTotalAsync());
            (await ReadAthleteCountAsync(path)).ShouldBe(1);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CatalogConnections_RunWithDocumentedPragmas()
    {
        var (store, factory, root) = CreateStore();
        try
        {
            ImportCatalogHandler handler = new(store);
            _ = await handler.HandleAsync(new ImportCatalogRequest(WhiteCardJson("White One")));
            string path = store.GetCatalogFilePath();

            // Per-connection pragmas (foreign keys, busy timeout) come from the
            // catalog interceptor on every opened EF connection.
            using CatalogDbContext context = factory.Create(path);
            await context.Database.OpenConnectionAsync();
            SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
            (await ReadScalarAsync(connection, "PRAGMA foreign_keys;")).ShouldBe(1L);
            (await ReadScalarAsync(connection, "PRAGMA busy_timeout;")).ShouldBe(5000L);
            await context.Database.CloseConnectionAsync();

            // WAL mode is file-persisted and was applied once on the schema path.
            using SqliteConnection raw = await OpenRawAsync(path);
            using DbCommand journal = raw.CreateCommand();
            journal.CommandText = "PRAGMA journal_mode;";
            (await journal.ExecuteScalarAsync()).ShouldBe("wal");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static (CatalogStore Store, CatalogDbContextFactory Factory, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-mss147-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<CatalogStorageOptions> options = Options.Create(new CatalogStorageOptions { CatalogPath = Path.Combine(root, "catalog.db") });
        CatalogDbContextFactory factory = new(new CatalogSqliteConnectionInterceptor());
        CatalogStore store = new(options, new TestHostEnvironment(root), factory);
        return (store, factory, root);
    }

    private static CatalogAthlete Athlete(string name, SportingColor color) => new(
        name,
        color,
        ["Human"],
        IsArtifact: false,
        HasDevoid: false,
        HasHybridMana: false,
        ["W"],
        "{W}",
        "Creature — Human",
        ImageUrl: null,
        SetCode: "tst");

    /// <summary>
    /// Replicates the pre-MSS-147 import behavior exactly: EnsureCreated
    /// schema, WAL pragma on the import connection, athlete rows, and no
    /// <c>__EFMigrationsHistory</c> table (EnsureCreated never wrote one).
    /// </summary>
    private static async Task CreateLegacyCatalogAsync(CatalogDbContextFactory factory, string path, IReadOnlyList<CatalogAthlete> athletes)
    {
        using CatalogDbContext context = factory.Create(path);
        _ = await context.Database.EnsureCreatedAsync().ConfigureAwait(false);
        _ = await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;").ConfigureAwait(false);
        using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
        context.Athletes.AddRange(athletes.Select(CatalogAthleteEntity.FromAthlete));
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static string WhiteCardJson(string name) => CardJson(name, "W");

    private static string BlackCardJson(string name) => CardJson(name, "B");

    private static string CardJson(string name, string colorLetter) => $$"""
        [
          {"name": "{{name}}", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{{"{" + colorLetter + "}"}}", "colors": ["{{colorLetter}}"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
        ]
        """;

    private static async Task InsertHistoryRowAsync(string path, string migrationId)
    {
        using SqliteConnection connection = await OpenRawAsync(path).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ($id, '10.0.12');";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = migrationId;
        command.Parameters.Add(parameter);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<List<string>> ReadHistoryIdsAsync(string path)
    {
        using SqliteConnection connection = await OpenRawAsync(path).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
        List<string> ids = [];
        using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<long> ReadAthleteCountAsync(string path)
    {
        using SqliteConnection connection = await OpenRawAsync(path).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"CatalogAthletes\";";
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<string> ReadJournalModeAsync(string path)
    {
        using SqliteConnection connection = await OpenRawAsync(path).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<object?> ReadScalarAsync(DbConnection connection, string pragma)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = pragma;
        return await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private static async Task<List<(string Type, string Name, string Sql)>> ReadSchemaObjectsAsync(string path)
    {
        using SqliteConnection connection = await OpenRawAsync(path).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND name NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock') ORDER BY type, name";
        List<(string Type, string Name, string Sql)> objects = [];
        using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            objects.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return objects;
    }

    private static async Task<SqliteConnection> OpenRawAsync(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
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

    /// <summary>
    /// The production per-connection pragma behavior (inherited) plus a record
    /// of every SQL command EF executes on the catalog connection.
    /// </summary>
    private sealed class RecordingCatalogInterceptor : CatalogSqliteConnectionInterceptor, IDbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        InterceptionResult<DbCommand> IDbCommandInterceptor.CommandCreating(CommandCorrelatedEventData eventData, InterceptionResult<DbCommand> result) => result;

        DbCommand IDbCommandInterceptor.CommandCreated(CommandEndEventData eventData, DbCommand result) => result;

        DbCommand IDbCommandInterceptor.CommandInitialized(CommandEndEventData eventData, DbCommand result) => result;

        void IDbCommandInterceptor.CommandCanceled(DbCommand command, CommandEndEventData eventData)
        {
        }

        Task IDbCommandInterceptor.CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken) => Task.CompletedTask;

        void IDbCommandInterceptor.CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
        }

        Task IDbCommandInterceptor.CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken) => Task.CompletedTask;

        InterceptionResult IDbCommandInterceptor.DataReaderClosing(DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result) => result;

        ValueTask<InterceptionResult> IDbCommandInterceptor.DataReaderClosingAsync(DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result) => ValueTask.FromResult(result);

        InterceptionResult IDbCommandInterceptor.DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result) => result;

        int IDbCommandInterceptor.NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result) => result;

        ValueTask<int> IDbCommandInterceptor.NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken) => ValueTask.FromResult(result);

        InterceptionResult<int> IDbCommandInterceptor.NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        ValueTask<InterceptionResult<int>> IDbCommandInterceptor.NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        DbDataReader IDbCommandInterceptor.ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) => result;

        ValueTask<DbDataReader> IDbCommandInterceptor.ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken) => ValueTask.FromResult(result);

        InterceptionResult<DbDataReader> IDbCommandInterceptor.ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        ValueTask<InterceptionResult<DbDataReader>> IDbCommandInterceptor.ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        object? IDbCommandInterceptor.ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result) => result;

        ValueTask<object?> IDbCommandInterceptor.ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken) => ValueTask.FromResult(result);

        InterceptionResult<object> IDbCommandInterceptor.ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Record(command);
            return result;
        }

        ValueTask<InterceptionResult<object>> IDbCommandInterceptor.ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command) => Commands.Add(command.CommandText);
    }
}
