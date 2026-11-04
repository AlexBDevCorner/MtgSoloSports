using System.Globalization;
using System.Text.Json;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.SaveSnapshot;

/// <summary>
/// Local read-only save snapshot helper (MSS-133). Data output (JSON) goes to
/// stdout; human logs go to stderr. Exit 0 on success, 1 on operational
/// failure, 2 on usage errors.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions OutputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static async Task<int> Main(string[] args)
    {
        return await RunAsync(args, httpHandlerOverride: null, Console.Out, Console.Error).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        string[] args,
        HttpMessageHandler? httpHandlerOverride,
        TextWriter stdout,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0 || IsHelp(args[0]))
        {
            await stdout.WriteAsync(Usage()).ConfigureAwait(false);
            return 0;
        }

        string command = args[0].Trim().ToLowerInvariant();
        string[] rest = args[1..];
        try
        {
            return command switch
            {
                "list-saves" => await ListSavesAsync(rest, httpHandlerOverride, stdout, stderr).ConfigureAwait(false),
                "acquire" => await AcquireAsync(rest, httpHandlerOverride, stdout, stderr).ConfigureAwait(false),
                "info" => Info(rest, stdout, stderr),
                "query" => await QueryAsync(rest, stdout, stderr).ConfigureAwait(false),
                "help" => await WriteHelpAsync(stdout).ConfigureAwait(false),
                _ => UsageError(stderr, $"Unknown command '{args[0]}'."),
            };
        }
        catch (ArgumentException ex)
        {
            // Usage-shaped failures (bad flags, forbidden SQL) exit 2.
            await stderr.WriteLineAsync($"[snapshot] usage error: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
        {
            await stderr.WriteLineAsync($"[snapshot] failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> ListSavesAsync(
        string[] args,
        HttpMessageHandler? httpHandlerOverride,
        TextWriter stdout,
        TextWriter stderr)
    {
        string? configPath = GetOption(args, "--config");
        string? baseUrl = GetOption(args, "--base-url") ?? TryBaseUrlFromConfig(configPath) ?? SnapshotConfig.ExampleBaseUrl;
        using HttpClient client = CreateHttpClient(httpHandlerOverride);
        await stderr.WriteLineAsync($"[snapshot] listing saves from '{baseUrl}'.").ConfigureAwait(false);
        var saves = await SnapshotAcquirer.ListSavesAsync(client, baseUrl).ConfigureAwait(false);
        await stderr.WriteLineAsync($"[snapshot] found {saves.Count} save(s); configure the exact save id, nothing is selected automatically.").ConfigureAwait(false);
        await stdout.WriteAsync(JsonSerializer.Serialize(new { saves }, OutputJsonOptions)).ConfigureAwait(false);
        await stdout.WriteAsync(Environment.NewLine).ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> AcquireAsync(
        string[] args,
        HttpMessageHandler? httpHandlerOverride,
        TextWriter stdout,
        TextWriter stderr)
    {
        string configPath = RequireOption(args, "--config");
        string? bundlePath = GetOption(args, "--bundle");
        string? baseUrlOverride = GetOption(args, "--base-url");
        Guid? saveIdOverride = ParseGuidOption(args, "--save-id");
        string? workingDirOverride = GetOption(args, "--working-dir");
        SnapshotConfig config = SnapshotConfig.Load(configPath, baseUrlOverride, saveIdOverride, workingDirOverride);

        SnapshotInfo info;
        if (!string.IsNullOrWhiteSpace(bundlePath))
        {
            string fullBundlePath = Path.GetFullPath(bundlePath);
            await stderr.WriteLineAsync($"[snapshot] acquiring save '{config.SaveId:D}' offline from bundle '{fullBundlePath}'.").ConfigureAwait(false);
            byte[] bundleBytes = await ReadBundleFileAsync(fullBundlePath).ConfigureAwait(false);
            info = await SnapshotAcquirer.AcquireFromBundleAsync(
                bundleBytes, config.SaveId, config.WorkingDirectory, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }
        else
        {
            await stderr.WriteLineAsync($"[snapshot] downloading export for save '{config.SaveId:D}' from '{config.BaseUrl}'.").ConfigureAwait(false);
            using HttpClient client = CreateHttpClient(httpHandlerOverride);
            info = await SnapshotAcquirer.AcquireFromHttpAsync(
                client, config.BaseUrl, config.SaveId, config.WorkingDirectory, DateTimeOffset.UtcNow).ConfigureAwait(false);
        }

        await stderr.WriteLineAsync(
            $"[snapshot] published working snapshot for save '{info.SaveId:D}' " +
            $"({info.SaveName}) at '{info.SnapshotPath}'.").ConfigureAwait(false);
        await stdout.WriteAsync(info.ToJson()).ConfigureAwait(false);
        await stdout.WriteAsync(Environment.NewLine).ConfigureAwait(false);
        return 0;
    }

    private static int Info(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string configPath = RequireOption(args, "--config");
        string? workingDirOverride = GetOption(args, "--working-dir");
        SnapshotConfig config = SnapshotConfig.Load(configPath, workingDirectoryOverride: workingDirOverride);
        SnapshotInfo info = SnapshotReader.ReadInfo(config.WorkingDirectory);
        stderr.WriteLine($"[snapshot] working snapshot for save '{info.SaveId:D}' at '{info.SnapshotPath}'.");
        stdout.Write(info.ToJson());
        stdout.Write(Environment.NewLine);
        return 0;
    }

    private static async Task<int> QueryAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string configPath = RequireOption(args, "--config");
        string sql = RequireOption(args, "--sql");
        string? workingDirOverride = GetOption(args, "--working-dir");
        int limit = ParseLimitOption(args);
        SnapshotConfig config = SnapshotConfig.Load(configPath, workingDirectoryOverride: workingDirOverride);
        SnapshotInfo info = SnapshotReader.ReadInfo(config.WorkingDirectory);
        SnapshotReader.QueryResult result = await SnapshotReader
            .QueryAsync(info.SnapshotPath, sql, limit)
            .ConfigureAwait(false);
        await stderr.WriteLineAsync(
            $"[snapshot] read-only query on save '{info.SaveId:D}' returned {result.Rows.Count} row(s)" +
            (result.Truncated ? $" (truncated to {limit})" : string.Empty) + ".").ConfigureAwait(false);
        await stdout.WriteAsync(JsonSerializer.Serialize(
            new
            {
                columns = result.Columns,
                rows = result.Rows,
                rowCount = result.Rows.Count,
                truncated = result.Truncated,
            },
            OutputJsonOptions)).ConfigureAwait(false);
        await stdout.WriteAsync(Environment.NewLine).ConfigureAwait(false);
        return 0;
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler? overrideHandler)
    {
        if (overrideHandler is not null)
        {
            return new HttpClient(overrideHandler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromMinutes(5),
            };
        }

        return new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    private static async Task<byte[]> ReadBundleFileAsync(string fullBundlePath)
    {
        long length;
        try
        {
            length = new FileInfo(fullBundlePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Offline bundle file could not be read: '{fullBundlePath}'.", ex);
        }

        if (length == 0)
        {
            throw new InvalidOperationException($"Offline bundle file is empty: '{fullBundlePath}'.");
        }

        if (length > SaveBundle.MaxBundleBytes)
        {
            throw new InvalidOperationException(
                $"Offline bundle exceeds the {SaveBundle.MaxBundleBytes} byte import limit: '{fullBundlePath}'.");
        }

        try
        {
            return await File.ReadAllBytesAsync(fullBundlePath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Offline bundle file could not be read: '{fullBundlePath}'.", ex);
        }
    }

    private static string? TryBaseUrlFromConfig(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return null;
        }

        try
        {
            return SnapshotConfig.Load(configPath).BaseUrl;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string RequireOption(string[] args, string name)
    {
        return GetOption(args, name)
            ?? throw new ArgumentException($"Missing required option '{name}'.", nameof(args));
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }

    private static Guid? ParseGuidOption(string[] args, string name)
    {
        string? raw = GetOption(args, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Guid.TryParse(raw.Trim(), out Guid parsed) || parsed.Equals(Guid.Empty))
        {
            throw new ArgumentException($"Option '{name}' must be a non-empty GUID.", nameof(args));
        }

        return parsed;
    }

    private static int ParseLimitOption(string[] args)
    {
        string? raw = GetOption(args, "--limit");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SnapshotReader.DefaultQueryLimit;
        }

        if (!int.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out int limit))
        {
            throw new ArgumentException("Option '--limit' must be an integer.", nameof(args));
        }

        return limit;
    }

    private static bool IsHelp(string value)
    {
        return string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> WriteHelpAsync(TextWriter stdout)
    {
        await stdout.WriteAsync(Usage()).ConfigureAwait(false);
        return 0;
    }

    private static int UsageError(TextWriter stderr, string message)
    {
        stderr.WriteLine($"[snapshot] usage error: {message}");
        stderr.WriteLine(Usage());
        return 2;
    }

    private static string Usage() =>
        "MtgSoloSports save snapshot helper (MSS-133): fetch one verified read-only\n" +
        "SQLite snapshot for the explicitly configured save.\n" +
        "\n" +
        "Usage:\n" +
        "  snapshot list-saves [--config <path>] [--base-url <url>]\n" +
        "  snapshot acquire --config <path> [--bundle <file>] [--base-url <url>] [--save-id <guid>] [--working-dir <path>]\n" +
        "  snapshot info --config <path> [--working-dir <path>]\n" +
        "  snapshot query --config <path> --sql \"<SELECT ...>\" [--limit <n>] [--working-dir <path>]\n" +
        "\n" +
        "The loopback game app is the only network access (GET /api/saves and\n" +
        "GET /api/saves/{id}/export); --bundle works fully offline. Inspection is\n" +
        "read-only (Mode=ReadOnly + query_only) and never migrates or simulates.\n" +
        "Data JSON goes to stdout, logs go to stderr.\n";
}
