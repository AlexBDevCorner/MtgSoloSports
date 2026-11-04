using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MtgSoloSports.SaveSnapshot;

/// <summary>
/// Read-only inspection of a validated working snapshot. Every connection uses
/// <c>Mode=ReadOnly</c> plus <c>PRAGMA query_only=ON</c> and never touches
/// application bootstrap, import, migration, seeding or simulation paths, so
/// sporting state, RNG, the source bundle, the manifest and the validated
/// database cannot be altered during inspection. SQLite may keep empty WAL
/// bookkeeping beside the file; the next acquire clears it, and hash
/// comparison proves the database bytes stay identical.
/// </summary>
public static class SnapshotReader
{
    public const int DefaultQueryLimit = 200;
    public const int MaxQueryLimit = 1000;

    public const string DatabaseFileName = "save.db";
    public const string ManifestFileName = "manifest.json";
    public const string InfoFileName = "snapshot.json";

    // Mutating keywords rejected in user-supplied SQL. The read-only connection
    // would reject writes anyway; this reports the problem as a usage error.
    private static readonly HashSet<string> ForbiddenKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert", "update", "delete", "drop", "alter", "create", "replace",
        "attach", "detach", "vacuum", "pragma", "transaction", "savepoint",
        "release", "reindex", "analyze",
    };

    /// <summary>
    /// Identity plus season/stage coverage read from the snapshot database.
    /// </summary>
    public sealed record DatabaseView(
        Guid SaveId,
        string SaveName,
        DateTimeOffset CreatedUtc,
        int SchemaVersion,
        int CurrentSeason,
        string Phase,
        int RulesVersion,
        string RulesJson,
        string RngAlgorithm,
        int RngVersion,
        IReadOnlyList<SeasonCoverage> Seasons);

    public sealed record RngView(string Algorithm, int AlgorithmVersion, ulong State, ulong Stream);

    public sealed record QueryResult(
        IReadOnlyList<string> Columns,
        IReadOnlyList<IReadOnlyList<object?>> Rows,
        bool Truncated);

    public static string DatabasePathFor(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        return Path.Combine(Path.GetFullPath(workingDirectory), DatabaseFileName);
    }

    public static string ManifestPathFor(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        return Path.Combine(Path.GetFullPath(workingDirectory), ManifestFileName);
    }

    public static string InfoPathFor(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        return Path.Combine(Path.GetFullPath(workingDirectory), InfoFileName);
    }

    /// <summary>
    /// Reads the published <c>snapshot.json</c> and proves it still describes
    /// the current <c>save.db</c>: a torn or interrupted refresh that replaced
    /// one file without the other fails here instead of silently serving stale
    /// data as current.
    /// </summary>
    public static SnapshotInfo ReadInfo(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        string root = Path.GetFullPath(workingDirectory);
        string infoPath = InfoPathFor(root);
        string databasePath = DatabasePathFor(root);
        if (!File.Exists(infoPath) || !File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                $"No working snapshot in '{root}'. Run 'acquire' for the configured save first.");
        }

        SnapshotInfo info;
        try
        {
            info = SnapshotInfo.FromJson(File.ReadAllText(infoPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Working snapshot metadata '{infoPath}' is unreadable. Re-run 'acquire'.", ex);
        }

        string actualSha = Persistence.Saves.SaveBundle.ComputeFileSha256Hex(databasePath);
        if (!string.Equals(actualSha, info.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Working snapshot is incomplete: 'snapshot.json' does not match 'save.db'. " +
                "A previous refresh did not finish publishing; re-run 'acquire'.");
        }

        return info;
    }

    public static DatabaseView ReadDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                $"Snapshot database was not found: '{databasePath}'. Run 'acquire' first.");
        }

        using SqliteConnection connection = OpenReadOnly(databasePath);
        using SqliteCommand metadataCommand = connection.CreateCommand();
        metadataCommand.CommandText =
            "SELECT SaveId, Name, CreatedUtc, SchemaVersion, CurrentSeason, Phase FROM SaveMetadata WHERE Id = 1;";
        using SqliteDataReader metadataReader = metadataCommand.ExecuteReader();
        if (!metadataReader.Read())
        {
            throw new InvalidOperationException("Snapshot database is missing required metadata rows.");
        }

        Guid saveId = ParseGuid(metadataReader.GetString(0), "SaveId");
        string name = metadataReader.GetString(1);
        DateTimeOffset createdUtc = ParseCreatedUtc(metadataReader.GetString(2));
        int schemaVersion = metadataReader.GetInt32(3);
        int currentSeason = metadataReader.GetInt32(4);
        string phase = metadataReader.GetString(5);

        (int rulesVersion, string rulesJson) = ReadRulesRow(connection);
        (string rngAlgorithm, int rngVersion) = ReadRngRow(connection);
        IReadOnlyList<SeasonCoverage> seasons = ReadCoverage(connection);
        return new DatabaseView(
            saveId, name, createdUtc, schemaVersion, currentSeason, phase,
            rulesVersion, rulesJson, rngAlgorithm, rngVersion, seasons);
    }

    public static RngView ReadRngState(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        using SqliteConnection connection = OpenReadOnly(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Algorithm, AlgorithmVersion, State, Stream FROM RngStates WHERE Id = 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("Snapshot database is missing the RNG state row.");
        }

        return new RngView(
            reader.GetString(0),
            reader.GetInt32(1),
            unchecked((ulong)reader.GetInt64(2)),
            unchecked((ulong)reader.GetInt64(3)));
    }

    /// <summary>
    /// Runs one bounded read-only SELECT (or WITH ... SELECT) against the
    /// working snapshot. Anything else is rejected before it reaches SQLite,
    /// and the read-only connection rejects writes even if validation were
    /// bypassed.
    /// </summary>
    public static async Task<QueryResult> QueryAsync(
        string databasePath,
        string sql,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string validated = EnsureReadOnlySelect(sql);
        if (limit <= 0 || limit > MaxQueryLimit)
        {
            throw new ArgumentException(
                $"Query limit must be between 1 and {MaxQueryLimit}.", nameof(limit));
        }

        if (!File.Exists(databasePath))
        {
            throw new InvalidOperationException(
                $"Snapshot database was not found: '{databasePath}'. Run 'acquire' first.");
        }

        using SqliteConnection connection = OpenReadOnly(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = validated;
        command.CommandTimeout = 30;
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        List<string> columns = new(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        List<IReadOnlyList<object?>> rows = [];
        bool truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (rows.Count >= limit)
            {
                truncated = true;
                break;
            }

            object?[] values = new object?[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
            {
                values[i] = ReadValue(reader, i);
            }

            rows.Add(values);
        }

        return new QueryResult(columns, rows, truncated);
    }

    internal static string EnsureReadOnlySelect(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new ArgumentException("Query SQL must not be empty.", nameof(sql));
        }

        string body = StripLeadingComments(sql.Trim());
        if (!StartsWithSelectOrWith(body))
        {
            throw new ArgumentException(
                "Only a single read-only SELECT (or WITH ... SELECT) statement is allowed.", nameof(sql));
        }

        string withoutTrailingSemicolon = body.TrimEnd();
        if (withoutTrailingSemicolon.EndsWith(';'))
        {
            withoutTrailingSemicolon = withoutTrailingSemicolon[..^1].TrimEnd();
        }

        if (withoutTrailingSemicolon.Contains(';'))
        {
            throw new ArgumentException("Only a single SQL statement is allowed.", nameof(sql));
        }

        string dequoted = StripStringLiterals(withoutTrailingSemicolon);
        if (ContainsForbiddenKeyword(dequoted))
        {
            throw new ArgumentException(
                "Query contains a forbidden mutating keyword; only SELECT reads are allowed.", nameof(sql));
        }

        return body;
    }

    private static bool ContainsForbiddenKeyword(string sql)
    {
        int tokenStart = -1;
        for (int i = 0; i <= sql.Length; i++)
        {
            bool isWordChar = i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_');
            if (isWordChar)
            {
                tokenStart = tokenStart < 0 ? i : tokenStart;
            }
            else if (tokenStart >= 0)
            {
                if (ForbiddenKeywords.Contains(sql[tokenStart..i]))
                {
                    return true;
                }

                tokenStart = -1;
            }
        }

        return false;
    }

    internal static SqliteConnection OpenReadOnly(string databasePath)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        SqliteConnection connection = new(builder.ConnectionString);
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static (int RulesVersion, string RulesJson) ReadRulesRow(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT RulesVersion, RulesJson FROM RulesSnapshots WHERE Id = 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("Snapshot database is missing the rules snapshot row.");
        }

        return (reader.GetInt32(0), reader.GetString(1));
    }

    private static (string Algorithm, int AlgorithmVersion) ReadRngRow(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Algorithm, AlgorithmVersion FROM RngStates WHERE Id = 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException("Snapshot database is missing the RNG state row.");
        }

        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static IReadOnlyList<SeasonCoverage> ReadCoverage(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Dictionary<int, (int SeasonNumber, bool HasSuperleague, bool IsComplete)> seasonsById = ReadSeasons(connection);
        Dictionary<int, (int StageCount, int CompletedStages)> stagesBySeason = ReadStageCounts(connection);
        Dictionary<int, int> roundsBySeason = ReadRoundCounts(connection);
        List<SeasonCoverage> coverage = new(seasonsById.Count);
        foreach (var (seasonId, season) in seasonsById.OrderBy(static entry => entry.Value.SeasonNumber))
        {
            _ = stagesBySeason.TryGetValue(seasonId, out (int StageCount, int CompletedStages) stages);
            _ = roundsBySeason.TryGetValue(seasonId, out int rounds);
            coverage.Add(new SeasonCoverage(
                season.SeasonNumber, season.HasSuperleague, season.IsComplete,
                stages.StageCount, stages.CompletedStages, rounds));
        }

        return coverage;
    }

    private static Dictionary<int, (int SeasonNumber, bool HasSuperleague, bool IsComplete)> ReadSeasons(SqliteConnection connection)
    {
        Dictionary<int, (int SeasonNumber, bool HasSuperleague, bool IsComplete)> seasons = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id, SeasonNumber, HasSuperleague, IsComplete FROM Seasons ORDER BY SeasonNumber;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            seasons[reader.GetInt32(0)] = (reader.GetInt32(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0);
        }

        return seasons;
    }

    private static Dictionary<int, (int StageCount, int CompletedStages)> ReadStageCounts(SqliteConnection connection)
    {
        Dictionary<int, (int StageCount, int CompletedStages)> counts = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SeasonId, COUNT(*), COALESCE(SUM(IsComplete), 0) FROM Stages GROUP BY SeasonId;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetInt32(0)] = (reader.GetInt32(1), (int)reader.GetInt64(2));
        }

        return counts;
    }

    private static Dictionary<int, int> ReadRoundCounts(SqliteConnection connection)
    {
        Dictionary<int, int> counts = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SeasonId, COUNT(*) FROM Rounds GROUP BY SeasonId;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[reader.GetInt32(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    private static object? ReadValue(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        object value = reader.GetValue(ordinal);
        return value switch
        {
            byte[] bytes => Convert.ToHexString(bytes).ToLowerInvariant(),
            long number => number,
            int number => number,
            double number => number,
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private static Guid ParseGuid(string raw, string column)
    {
        if (Guid.TryParse(raw, out Guid parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }

        throw new InvalidOperationException($"Snapshot database has an invalid '{column}' value.");
    }

    private static DateTimeOffset ParseCreatedUtc(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException("Snapshot database has an invalid 'CreatedUtc' value.");
    }

    private static string StripLeadingComments(string sql)
    {
        string rest = sql;
        while (true)
        {
            if (rest.StartsWith("--", StringComparison.Ordinal))
            {
                int newline = rest.IndexOf('\n');
                rest = newline < 0 ? string.Empty : rest[(newline + 1)..].TrimStart();
                continue;
            }

            if (rest.StartsWith("/*", StringComparison.Ordinal))
            {
                int end = rest.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0)
                {
                    return string.Empty;
                }

                rest = rest[(end + 2)..].TrimStart();
                continue;
            }

            return rest;
        }
    }

    private static bool StartsWithSelectOrWith(string sql)
    {
        return sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && (sql.Length == 6 || !char.IsLetterOrDigit(sql[6]))
            || sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            && (sql.Length == 4 || !char.IsLetterOrDigit(sql[4]));
    }

    private static string StripStringLiterals(string sql)
    {
        System.Text.StringBuilder builder = new(sql.Length);
        bool inLiteral = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (!inLiteral)
            {
                if (c == '\'')
                {
                    inLiteral = true;
                    builder.Append(' ');
                }
                else
                {
                    builder.Append(c);
                }
            }
            else if (c == '\'')
            {
                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i++;
                }
                else
                {
                    inLiteral = false;
                }
            }
        }

        return builder.ToString();
    }
}
