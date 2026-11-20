using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Applies local SQLite connection behavior on every opened save connection:
/// foreign keys on, bounded busy timeout for brief local contention, and
/// WAL-recommended synchronous=NORMAL durability. synchronous is per-connection
/// (SQLite resets it to FULL on every open), so this interceptor is the single
/// place that keeps the documented intent real for every save connection.
/// WAL journal mode itself is a file-persisted property set per save file at
/// creation time.
/// </summary>
public sealed class SaveSqliteConnectionInterceptor : DbConnectionInterceptor
{
    private const string Pragmas = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";

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
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    private static async Task SetPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = Pragmas;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
