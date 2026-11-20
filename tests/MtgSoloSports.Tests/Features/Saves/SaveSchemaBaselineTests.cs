using System.Data.Common;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Saves;

/// <summary>
/// MSS-149 proof that new saves take the single-step schema baseline while
/// existing saves keep the full migration chain: fresh creation issues no
/// EF migration machinery and stamps the complete chain history; the
/// baseline-created database is schema-identical (sqlite_master DDL,
/// including EF bookkeeping tables) to a full chain replay with identical
/// history rows; a fabricated pending migration on a fresh save still
/// exercises the checkpoint/migrate/rollback recovery path with rules
/// bytes untouched; and the committed baseline artifact can be regenerated
/// through one documented, explicitly requested command.
/// </summary>
public sealed partial class SaveSchemaBaselineTests
{
    private const string RegenVariable = "MTGSS_REGEN_SAVE_SCHEMA_BASELINE";

    [Fact]
    public async Task FreshSaveCreation_UsesTheSingleBaselineStep_NotTheMigrationChain()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-mss149-fast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RecordingSaveCommandInterceptor recorder = new();
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        SaveDbContextFactory factory = new(recorder);
        SaveStore store = new(options, new TestHostEnvironment(root), factory, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<SaveStore>.Instance);
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Fast Create", 201149UL, 201150UL, UniverseTestCatalog.Build());

            // The baseline runs on a raw SQLite connection below EF's command
            // pipeline, so the EF pipeline must show no migration machinery:
            // no per-migration history INSERTs and no schema DDL. Chain
            // replay would emit both through this interceptor.
            recorder.Commands.ShouldNotBeEmpty();
            recorder.Commands.ShouldAllBe(static command =>
                !command.Contains("INSERT INTO \"__EFMigrationsHistory\"", StringComparison.Ordinal)
                && !SchemaDdlRegex().IsMatch(command));

            // The save is fully usable and carries the complete stamped
            // chain history, so future migrations treat it like a migrated save.
            _ = await store.OpenAsync(created.Detail.SaveId);
            List<string> expectedIds = AllMigrationIds();
            (await ReadHistoryIdsAsync(store.GetSaveFilePath(created.Detail.SaveId))).ShouldBe(expectedIds);
            (await SaveSchemaMigrator.GetPendingMigrationsAsync(
                factory, store.GetSaveFilePath(created.Detail.SaveId), CancellationToken.None))
                .ShouldBeEmpty("a baseline-created save must have no pending migrations");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task BaselineCreatedSchema_IsExactlyTheFullChainReplaySchema()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-mss149-equiv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SaveDbContextFactory factory = new(new SaveSqliteConnectionInterceptor());
        try
        {
            string chainPath = Path.Combine(root, "chain.db");
            using (SaveDbContext context = factory.Create(chainPath))
            {
                await context.Database.MigrateAsync();
            }

            string baselinePath = Path.Combine(root, "baseline.db");
            await SaveSchemaBaseline.ApplyAsync(factory, baselinePath);

            // Tables, columns (in chain order), indexes, checks and EF
            // bookkeeping tables are byte-identical in their stored DDL.
            List<(string Type, string Name, string Sql)> chainSchema = await ReadSchemaObjectsAsync(chainPath);
            List<(string Type, string Name, string Sql)> baselineSchema = await ReadSchemaObjectsAsync(baselinePath);
            baselineSchema.ShouldBe(chainSchema);

            // The migration histories match as well: same identifiers in the
            // same order, same recorded product version.
            (await ReadHistoryRowsAsync(chainPath)).ShouldBe(await ReadHistoryRowsAsync(baselinePath));

            // Both databases are equally current from the migrator's view.
            (await SaveSchemaMigrator.GetPendingMigrationsAsync(factory, chainPath, CancellationToken.None)).ShouldBeEmpty();
            (await SaveSchemaMigrator.GetPendingMigrationsAsync(factory, baselinePath, CancellationToken.None)).ShouldBeEmpty();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task FailingPendingMigration_OnBaselineSave_RollsBackToCheckpointWithRulesBytesUntouched()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-mss149-rollback-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Failing Migration", 201151UL, 201152UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            string path = store.GetSaveFilePath(saveId);
            string failingMigrationId = AllMigrationIds()[^1];

            // Fabricate a pre-baseline-era save whose final migration is
            // pending again; re-applying it fails loudly because the column
            // already exists at the baseline.
            await ExecuteRawAsync(path, $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{failingMigrationId}';");
            (await ReadHistoryIdsAsync(path)).Contains(failingMigrationId, StringComparer.Ordinal).ShouldBeFalse();

            MtgSoloSports.Shared.SaveCorruptionException failure = await Should.ThrowAsync<MtgSoloSports.Shared.SaveCorruptionException>(
                () => store.EnsureMigratedAsync(saveId));
            failure.Message.ShouldContain("rolled back to verified checkpoint");

            // The rollback restored the exact pre-migration bytes: rules,
            // RNG and metadata are untouched and no silent repair happened.
            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(saveId);
            reopened.ShouldBe(created.Detail);
            reopened.RulesJson.ShouldBe(created.Detail.RulesJson);
            reopened.RngState.ShouldBe(created.Detail.RngState);
            reopened.RngStream.ShouldBe(created.Detail.RngStream);
            (await ReadHistoryIdsAsync(path)).Contains(failingMigrationId, StringComparer.Ordinal).ShouldBeFalse();

            IReadOnlyList<SaveStore.CheckpointRecord> checkpoints = await store.ListCheckpointsAsync(saveId);
            checkpoints.ShouldContain(checkpoint => checkpoint.Reason == "pre-schema-migration-to-1");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task BaselineMarker_MustNameItsChainCutoff()
    {
        string baseline = await SaveSchemaBaseline.LoadBaselineAsync();
        string marker = SaveSchemaBaseline.ParseBaselineMarker(baseline);
        marker.ShouldBe(AllMigrationIds()[^1]);

        Should.Throw<InvalidOperationException>(() => SaveSchemaBaseline.ParseBaselineMarker("CREATE TABLE x;"));
        Should.Throw<InvalidOperationException>(() => SaveSchemaBaseline.ParseBaselineMarker(SaveSchemaBaseline.MarkerPrefix + "  "));
    }

    /// <summary>
    /// Regenerates the committed baseline artifact. Never runs in CI or
    /// normal development: it only writes repository files when the explicit
    /// environment variable is set, as documented in the migrations README.
    /// </summary>
    [Fact]
    public async Task RegenerateBaseline_OnlyWhenExplicitlyRequested()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RegenVariable), "1", StringComparison.Ordinal))
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-mss149-regen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        SaveDbContextFactory factory = new(new SaveSqliteConnectionInterceptor());
        try
        {
            string chainPath = Path.Combine(root, "chain.db");
            using (SaveDbContext context = factory.Create(chainPath))
            {
                await context.Database.MigrateAsync();
            }

            List<(string Type, string Name, string Sql)> captured = await ReadSchemaObjectsForCaptureAsync(chainPath);
            string marker = AllMigrationIds()[^1];
            StringBuilder sql = new();
            sql.Append(SaveSchemaBaseline.MarkerPrefix).Append(marker).Append('\n');
            foreach ((string type, string name, string ddl) in captured)
            {
                sql.Append(ddl).Append(";\n");
            }

            string target = FindBaselineArtifact();
            await File.WriteAllTextAsync(target, sql.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^\s*(?:CREATE\s+(?:TABLE|INDEX|UNIQUE\s+INDEX)|ALTER\s+TABLE)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 5000)]
    private static partial System.Text.RegularExpressions.Regex SchemaDdlRegex();

    private static List<string> AllMigrationIds()
    {
        DbContextOptions<SaveDbContext> options = new DbContextOptionsBuilder<SaveDbContext>()
            .UseSqlite("Data Source=never.db")
            .Options;
        using SaveDbContext context = new(options);
        return context.Database.GetMigrations().ToList();
    }

    private static async Task<List<string>> ReadHistoryIdsAsync(string path)
        => (await ReadHistoryRowsAsync(path).ConfigureAwait(false)).Select(row => row.Id).ToList();

    private static async Task<List<(string Id, string ProductVersion)>> ReadHistoryRowsAsync(string path)
    {
        using SqliteConnection connection = await OpenRawAsync(path).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT \"MigrationId\", \"ProductVersion\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
        List<(string, string)> rows = [];
        using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    private static async Task<List<(string Type, string Name, string Sql)>> ReadSchemaObjectsAsync(string path)
    {
        using SqliteConnection connection = await OpenRawAsync(path).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type, name";
        List<(string, string, string)> objects = [];
        using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            objects.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return objects;
    }

    private static async Task<List<(string Type, string Name, string Sql)>> ReadSchemaObjectsForCaptureAsync(string path)
    {
        using SqliteConnection connection = await OpenRawAsync(path).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY CASE WHEN type = 'table' THEN 0 ELSE 1 END, name";
        List<(string, string, string)> objects = [];
        using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            objects.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return objects;
    }

    private static async Task ExecuteRawAsync(string path, string sql)
    {
        using SqliteConnection connection = await OpenRawAsync(path).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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

    private static string FindBaselineArtifact()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string[] matches = Directory.GetFiles(directory.FullName, "SaveSchemaBaseline.sql", SearchOption.AllDirectories);
            if (matches.Length > 0)
            {
                return matches[0];
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The committed save schema baseline artifact was not found.");
    }

    /// <summary>
    /// The production per-connection pragma behavior (inherited) plus a
    /// record of every SQL command EF executes on the save connection.
    /// </summary>
    private sealed class RecordingSaveCommandInterceptor : SaveSqliteConnectionInterceptor, IDbCommandInterceptor
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

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";

        public string ContentRootPath { get; set; }

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
