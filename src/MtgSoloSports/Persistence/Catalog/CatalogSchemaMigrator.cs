using System.Data.Common;
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MtgSoloSports.Persistence.Catalog;

/// <summary>
/// Database-schema migration runner for the shared catalog file. Creates the
/// schema once through EF Core migrations, adopts catalogs created before
/// migrations existed (plain <c>EnsureCreated</c> schema, no history table)
/// by verifying their shape and baselining them onto the initial migration,
/// and applies pending migrations to already-migrated catalogs. When the file
/// cannot be served or migrated by this application version it fails loudly
/// with re-import guidance; it never silently auto-recreates or destroys an
/// existing catalog. Schema-only by construction: this runner never reads or
/// writes athlete rows and never touches game-rule state.
/// It runs on the creation/migration paths only: the import path (the only
/// catalog file creator) and the once-per-process read gate in
/// <see cref="CatalogStore"/>; steady-state reads bypass it entirely, so
/// schema work never repeats per operation.
/// </summary>
public static class CatalogSchemaMigrator
{
    private const string HistoryTableName = "__EFMigrationsHistory";
    private const string AthletesTableName = "CatalogAthletes";

    /// <summary>
    /// Identifier of the initial catalog migration. Catalogs created before
    /// migrations existed are baselined onto this migration after their
    /// shape is verified, so they keep working without data loss.
    /// </summary>
    public const string InitialMigrationId = "20261120071358_InitialCatalogSchema";

    /// <summary>
    /// Column set of the pre-migration catalog schema produced by
    /// <c>EnsureCreated</c>. The legacy-baseline path requires an exact match:
    /// extra or missing columns mean the file is damaged or comes from a
    /// version this application cannot reason about.
    /// </summary>
    private static readonly string[] LegacyInitialColumns =
    [
        "Id", "Name", "SportingColor", "CreatureTypesJson", "IsArtifact",
        "HasDevoid", "HasHybridMana", "FrontColors", "ManaCost", "TypeLine",
        "ImageUrl", "SetCode"
    ];

    /// <summary>
    /// Serializes schema work for the one catalog file per application so
    /// parallel imports cannot interleave history checks, baselining or
    /// migration writes. Imports themselves remain last-wins atomic replaces.
    /// </summary>
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);

    /// <summary>
    /// Ensures the catalog file exists with a schema served by the current
    /// application version: fresh files are created directly by EF Core
    /// migrations, legacy files are baselined first, and migrated files get
    /// pending migrations applied. Callers invoke this from the import path
    /// (file creation) and the once-per-process read gate in
    /// <see cref="CatalogStore"/>; every failure is a loud
    /// <see cref="CatalogSchemaIncompatibleException"/> with re-import guidance.
    /// </summary>
    public static async Task EnsureSchemaAsync(
        CatalogDbContextFactory factory,
        string catalogFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogFilePath);

        string? directory = Path.GetDirectoryName(catalogFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await SchemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(catalogFilePath))
            {
                // Fresh install: MigrateAsync creates the file, the history
                // table and the schema in one transaction. The same
                // post-creation invariant as the upgrade path applies, so a
                // stale or incomplete initial migration fails loudly here
                // instead of surfacing later as runtime "no such column".
                using CatalogDbContext context = factory.Create(catalogFilePath);
                await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                await EnsureServesCurrentModelAsync(factory, catalogFilePath, cancellationToken).ConfigureAwait(false);
                await ApplyWalModeAsync(catalogFilePath, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool hasHistory = await TableExistsAsync(catalogFilePath, HistoryTableName, cancellationToken).ConfigureAwait(false);
            if (!hasHistory)
            {
                bool hasAthletes = await TableExistsAsync(catalogFilePath, AthletesTableName, cancellationToken).ConfigureAwait(false);
                if (hasAthletes)
                {
                    // Pre-migration catalog: verify the legacy shape, then
                    // baseline it onto the initial migration without data loss.
                    await BaselineLegacyCatalogAsync(factory, catalogFilePath, cancellationToken).ConfigureAwait(false);
                }
                // else: an empty or stray file; MigrateAsync below creates the schema.
            }
            else
            {
                await GuardAgainstNewerCatalogAsync(factory, catalogFilePath, cancellationToken).ConfigureAwait(false);
            }

            using CatalogDbContext migrateContext = factory.Create(catalogFilePath);
            await migrateContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

            await EnsureServesCurrentModelAsync(factory, catalogFilePath, cancellationToken).ConfigureAwait(false);
            await ApplyWalModeAsync(catalogFilePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    /// <summary>
    /// Adopts a catalog created before migrations existed: the table shape
    /// must exactly match the pre-migration schema, then the EF Core history
    /// table and the initial-migration row are written in one transaction so
    /// subsequent model changes flow through normal migrations. Existing
    /// athlete rows are untouched.
    /// </summary>
    private static async Task BaselineLegacyCatalogAsync(
        CatalogDbContextFactory factory,
        string catalogFilePath,
        CancellationToken cancellationToken)
    {
        List<string> columns = await ReadTableColumnsAsync(catalogFilePath, AthletesTableName, cancellationToken).ConfigureAwait(false);
        if (!new HashSet<string>(columns, StringComparer.Ordinal).SetEquals(LegacyInitialColumns))
        {
            throw new CatalogSchemaIncompatibleException(
                $"The existing catalog database at '{catalogFilePath}' does not match the pre-migration catalog schema this application can adopt "
                + $"(expected columns [{string.Join(", ", LegacyInitialColumns)}], found [{string.Join(", ", columns)}]); it is damaged or comes from an unknown application version. "
                + ReImportGuidance(catalogFilePath));
        }

        using CatalogDbContext context = factory.Create(catalogFilePath);
        IHistoryRepository history = context.GetService<IHistoryRepository>();
        string createHistoryScript = history.GetCreateIfNotExistsScript();
        string insertHistoryScript = history.GetInsertScript(new HistoryRow(InitialMigrationId, EfCoreProductVersion()));

        using SqliteConnection connection = await OpenMetaConnectionAsync(catalogFilePath, cancellationToken).ConfigureAwait(false);
        using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (DbCommand create = connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = createHistoryScript;
                _ = await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (DbCommand insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = insertHistoryScript;
                _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // CancellationToken.None: the original failure, not a canceled
            // token, must propagate after the rollback attempt.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A catalog containing migration identifiers this application does not
    /// define was created by a newer application version and must never be
    /// silently upgraded or downgraded; failing loudly preserves the data for
    /// the version that understands it.
    /// </summary>
    private static async Task GuardAgainstNewerCatalogAsync(
        CatalogDbContextFactory factory,
        string catalogFilePath,
        CancellationToken cancellationToken)
    {
        List<string> applied = await ReadAppliedMigrationIdsAsync(catalogFilePath, cancellationToken).ConfigureAwait(false);
        using CatalogDbContext context = factory.Create(catalogFilePath);
        HashSet<string> known = new(context.Database.GetMigrations(), StringComparer.OrdinalIgnoreCase);
        List<string> unknown = applied.Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            throw new CatalogSchemaIncompatibleException(
                $"The catalog database at '{catalogFilePath}' was created by a newer application version and contains schema migration(s) this application does not know "
                + $"([{string.Join(", ", unknown)}]). Upgrade the application, or {ReImportGuidance(catalogFilePath).TrimStart()}");
        }
    }

    /// <summary>
    /// Post-migration invariant: the persisted table must expose every column
    /// the current model maps, so any schema/model divergence fails loudly at
    /// the schema path with re-import guidance instead of surfacing later as
    /// runtime "no such column" errors. Extra columns (from newer versions or
    /// intentional migration divergence) are tolerated and ignored.
    /// </summary>
    private static async Task EnsureServesCurrentModelAsync(
        CatalogDbContextFactory factory,
        string catalogFilePath,
        CancellationToken cancellationToken)
    {
        using CatalogDbContext context = factory.Create(catalogFilePath);
        IEntityType entity = context.Model.FindEntityType(typeof(CatalogAthleteEntity))
            ?? throw new InvalidOperationException("The catalog model is missing CatalogAthleteEntity.");
        StoreObjectIdentifier table = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)
            ?? throw new InvalidOperationException("The catalog model is missing the CatalogAthletes table mapping.");
        List<string?> expected = entity.GetProperties().Select(property => property.GetColumnName(in table)).ToList();
        List<string> actual = await ReadTableColumnsAsync(catalogFilePath, AthletesTableName, cancellationToken).ConfigureAwait(false);
        List<string> missing = expected
            .Where(column => column is not null && !actual.Contains(column, StringComparer.Ordinal))
            .ToList()!;
        if (missing.Count > 0)
        {
            throw new CatalogSchemaIncompatibleException(
                $"The catalog database at '{catalogFilePath}' is missing column(s) [{string.Join(", ", missing)}] required by this application version; a catalog migration is missing or did not apply correctly. "
                + ReImportGuidance(catalogFilePath));
        }
    }

    /// <summary>
    /// WAL journal mode is a file-persisted property, so it is applied once
    /// on this schema path (creation, baselining or migration) — identical to
    /// saves, which set WAL per save file at creation time and keep concurrent
    /// read/import access working. synchronous=NORMAL is the recommended WAL
    /// trade-off for the import connection, again mirroring the save flow.
    /// Per-connection settings that SQLite resets on every open connection
    /// (foreign keys, busy timeout) live in
    /// <see cref="CatalogSqliteConnectionInterceptor"/> instead.
    /// </summary>
    private static async Task ApplyWalModeAsync(string catalogFilePath, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = await OpenMetaConnectionAsync(catalogFilePath, cancellationToken).ConfigureAwait(false);
        using DbCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        _ = await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(string catalogFilePath, string tableName, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = await OpenMetaConnectionAsync(catalogFilePath, cancellationToken).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<List<string>> ReadTableColumnsAsync(
        string catalogFilePath,
        string tableName,
        CancellationToken cancellationToken)
    {
        using SqliteConnection connection = await OpenMetaConnectionAsync(catalogFilePath, cancellationToken).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName}');";
        List<string> columns = [];
        using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<List<string>> ReadAppliedMigrationIdsAsync(string catalogFilePath, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = await OpenMetaConnectionAsync(catalogFilePath, cancellationToken).ConfigureAwait(false);
        using DbCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT \"MigrationId\" FROM \"{HistoryTableName}\"";
        List<string> applied = [];
        using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    /// <summary>
    /// Short-lived unpooled meta connection for schema inspection and the
    /// baseline write, mirroring the save flow's raw-SQLite usage; the
    /// busy timeout matches the interceptor so local contention is bounded.
    /// </summary>
    private static async Task<SqliteConnection> OpenMetaConnectionAsync(string catalogFilePath, CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = catalogFilePath,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using DbCommand busy = connection.CreateCommand();
            busy.CommandText = "PRAGMA busy_timeout=5000;";
            _ = await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The EF Core product version recorded for manually baselined history
    /// rows, mirroring what EF Core itself writes when applying migrations
    /// (the informational version without source-control metadata).
    /// </summary>
    private static string EfCoreProductVersion()
    {
        string informational = typeof(Migration).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        int metadataIndex = informational.IndexOf('+');
        return metadataIndex > 0 ? informational[..metadataIndex] : informational;
    }

    private static string ReImportGuidance(string catalogFilePath) =>
        $"The catalog is fully re-importable: delete '{catalogFilePath}' and run the catalog import again; "
        + "existing saves are unaffected because every save keeps its own athlete snapshot.";
}
