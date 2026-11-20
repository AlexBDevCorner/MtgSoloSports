using System.Data.Common;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Single-step creation of new save databases at the current schema
/// baseline. The committed DDL in <c>SaveSchemaBaseline.sql</c> is captured
/// from a full migration-chain replay, so applying it in one batched
/// command reproduces the exact schema (tables, columns in chain order,
/// indexes, checks) that replaying every historical migration would
/// produce, then stamps <c>__EFMigrationsHistory</c> with the covered
/// migration identifiers so new saves and migrated saves are
/// indistinguishable to <see cref="SaveSchemaMigrator"/> and to future
/// migrations. Existing saves never take this path; they keep migrating
/// through the full chain.
/// This is schema-only by construction: it never touches rules or RNG
/// rows, and it never runs on an existing file.
/// </summary>
public static class SaveSchemaBaseline
{
    /// <summary>Manifest resource name of the committed baseline DDL.</summary>
    public const string ResourceName = "MtgSoloSports.Persistence.Saves.SaveSchemaBaseline.sql";

    /// <summary>
    /// Marker comment prefix on the first baseline line. The value after the
    /// prefix is the identifier of the last migration the baseline covers;
    /// history stamping stops there so a stale baseline can never claim
    /// migrations it does not contain.
    /// </summary>
    public const string MarkerPrefix = "-- save-schema-baseline-through: ";

    /// <summary>
    /// Creates the save database schema in one baseline step: one batched
    /// DDL command plus one batched history-stamp command inside a single
    /// transaction, followed by a loud staleness guard. Fails loudly with
    /// regeneration guidance when the application defines migrations newer
    /// than the committed baseline, so a forgotten regeneration can never
    /// produce a new save whose schema lies about its migration history.
    /// </summary>
    public static async Task ApplyAsync(
        SaveDbContextFactory factory,
        string saveFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveFilePath);
        if (File.Exists(saveFilePath))
        {
            throw new InvalidOperationException(
                $"Save schema baseline creation requires a new file; '{saveFilePath}' already exists.");
        }

        string baseline = await LoadBaselineAsync(cancellationToken).ConfigureAwait(false);
        string baselineThroughId = ParseBaselineMarker(baseline);
        IReadOnlyList<string> coveredIds = CollectCoveredMigrationIds(factory, baselineThroughId);
        string stampScript = BuildHistoryStampScript(factory, coveredIds);

        string? directory = Path.GetDirectoryName(saveFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using SqliteConnection connection = await OpenBaselineConnectionAsync(saveFilePath, cancellationToken).ConfigureAwait(false);
        using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (DbCommand schema = connection.CreateCommand())
            {
                schema.Transaction = transaction;
                schema.CommandText = baseline;
                _ = await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (DbCommand stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = stampScript;
                _ = await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

        await EnsureBaselineCoversCurrentChainAsync(factory, saveFilePath, baselineThroughId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the committed baseline DDL from the assembly manifest resource.
    /// </summary>
    public static async Task<string> LoadBaselineAsync(CancellationToken cancellationToken = default)
    {
        Stream? stream = typeof(SaveSchemaBaseline).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"The save schema baseline resource '{ResourceName}' is missing; new saves cannot be created.");
        }

        using Stream owned = stream;
        using StreamReader reader = new(owned, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the chain marker from the first baseline line.
    /// </summary>
    public static string ParseBaselineMarker(string baseline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline);
        int lineEnd = baseline.IndexOf('\n');
        string firstLine = (lineEnd < 0 ? baseline : baseline[..lineEnd]).TrimEnd('\r');
        if (!firstLine.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The save schema baseline must start with '{MarkerPrefix}<migrationId>'; regenerate the baseline (see the migrations README).");
        }

        string id = firstLine[MarkerPrefix.Length..].Trim();
        if (id.Length == 0)
        {
            throw new InvalidOperationException(
                "The save schema baseline marker does not name a migration; regenerate the baseline (see the migrations README).");
        }

        return id;
    }

    /// <summary>
    /// Migration identifiers defined by the application, ordered
    /// ordinally, limited to the baseline chain marker.
    /// </summary>
    private static IReadOnlyList<string> CollectCoveredMigrationIds(SaveDbContextFactory factory, string baselineThroughId)
    {
        using SaveDbContext context = factory.Create(Path.Combine(Path.GetTempPath(), "never.db"));
        List<string> covered = context.Database
            .GetMigrations()
            .Where(id => string.CompareOrdinal(id, baselineThroughId) <= 0)
            .ToList();
        if (covered.Count == 0)
        {
            throw new InvalidOperationException(
                $"The save schema baseline marker '{baselineThroughId}' does not match any defined migration; regenerate the baseline (see the migrations README).");
        }

        return covered;
    }

    private static string BuildHistoryStampScript(SaveDbContextFactory factory, IReadOnlyList<string> coveredIds)
    {
        using SaveDbContext context = factory.Create(Path.Combine(Path.GetTempPath(), "never.db"));
        IHistoryRepository history = context.GetService<IHistoryRepository>();
        string productVersion = EfCoreProductVersion();
        StringBuilder script = new();
        foreach (string id in coveredIds)
        {
            script.Append(history.GetInsertScript(new HistoryRow(id, productVersion)));
        }

        return script.ToString();
    }

    /// <summary>
    /// Loud staleness guard: after the baseline plus its history stamp, no
    /// migration may remain pending. A pending migration here means the
    /// application defines migrations newer than the committed baseline, so
    /// fresh saves must not be created until the baseline is regenerated;
    /// stamping the newer identifiers anyway would produce a database whose
    /// schema silently disagrees with its migration history.
    /// </summary>
    private static async Task EnsureBaselineCoversCurrentChainAsync(
        SaveDbContextFactory factory,
        string saveFilePath,
        string baselineThroughId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> pending = await SaveSchemaMigrator
            .GetPendingMigrationsAsync(factory, saveFilePath, cancellationToken)
            .ConfigureAwait(false);
        if (pending.Count > 0)
        {
            throw new InvalidOperationException(
                $"The save schema baseline is stale: it covers the migration chain through '{baselineThroughId}' but the application defines newer migration(s) "
                + $"[{string.Join(", ", pending)}]. Regenerate the baseline as described in src/MtgSoloSports/Persistence/Saves/Migrations/README.md before creating new saves.");
        }
    }

    /// <summary>
    /// The EF Core product version recorded for baseline history rows,
    /// mirroring what EF Core itself writes when applying migrations (the
    /// informational version without source-control metadata).
    /// </summary>
    private static string EfCoreProductVersion()
    {
        string informational = typeof(Migration).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty;
        int metadataIndex = informational.IndexOf('+');
        return metadataIndex > 0 ? informational[..metadataIndex] : informational;
    }

    /// <summary>
    /// Short-lived unpooled connection for the one-shot baseline step,
    /// mirroring the save flow's raw-SQLite usage; the busy timeout matches
    /// the interceptor so local contention is bounded.
    /// </summary>
    private static async Task<SqliteConnection> OpenBaselineConnectionAsync(
        string saveFilePath,
        CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = saveFilePath,
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
}
