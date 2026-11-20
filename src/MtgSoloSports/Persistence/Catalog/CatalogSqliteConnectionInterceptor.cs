using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MtgSoloSports.Persistence.Catalog;

/// <summary>
/// Applies local SQLite concurrency behavior on every opened catalog
/// connection (foreign keys on, busy timeout). Mirrors the save flow's
/// interceptor so both database families run on the same per-connection
/// defaults. WAL journal mode is deliberately NOT set here: journal_mode is a
/// file-persisted property, so <see cref="CatalogSchemaMigrator"/> applies WAL
/// once when the catalog schema is created or baselined (identical to saves,
/// which set WAL per file at creation time), while this interceptor only
/// re-applies the settings SQLite resets on every new connection.
/// Not sealed so test doubles can layer command recording on this pragma
/// behavior without a second registration point.
/// </summary>
public class CatalogSqliteConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(connection);
        SetPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await SetPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    private static void SetPragmas(DbConnection connection)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
    }

    private static async Task SetPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
