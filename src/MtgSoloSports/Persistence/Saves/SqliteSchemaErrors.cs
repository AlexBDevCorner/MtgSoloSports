using Microsoft.Data.Sqlite;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Narrow SQLite schema-error matching for legacy-save backfills. Only an
/// exact "no such table: {name}" error means the save schema predates the
/// feature migration; a "no such column" defect (or any other SQL error) on
/// an already-migrated save must fail loudly instead of being swallowed.
/// </summary>
public static class SqliteSchemaErrors
{
    public static bool IsMissingTable(SqliteException exception, string tableName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return exception.SqliteErrorCode == 1
            && exception.Message.Contains($"no such table: {tableName}", StringComparison.OrdinalIgnoreCase);
    }
}
