using System.Globalization;
using System.Text.Json;

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
                "evidence" => await EvidenceAsync(rest, stdout, stderr).ConfigureAwait(false),
                "archive" => await ArchiveAsync(rest, stdout, stderr).ConfigureAwait(false),
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
            ValidateBundleFile(fullBundlePath);
            info = await SnapshotAcquirer.AcquireFromBundleFileAsync(
                fullBundlePath, config.SaveId, config.WorkingDirectory, DateTimeOffset.UtcNow).ConfigureAwait(false);
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

    private static async Task<int> EvidenceAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string configPath = RequireOption(args, "--config");
        string? workingDirOverride = GetOption(args, "--working-dir");
        int? season = ParseIntOption(args, "--season");
        int? stage = ParseIntOption(args, "--stage");
        string? athlete = GetOption(args, "--athlete");
        int? athleteId = ParseIntOption(args, "--athlete-id");
        SnapshotConfig config = SnapshotConfig.Load(configPath, workingDirectoryOverride: workingDirOverride);
        SnapshotInfo info = SnapshotReader.ReadInfo(config.WorkingDirectory);
        StageEvidenceDocument evidence = await StageEvidenceBuilder
            .BuildAsync(info.SnapshotPath, info, season, stage, athlete, athleteId)
            .ConfigureAwait(false);
        await stderr.WriteLineAsync(
            $"[snapshot] stage evidence for save '{info.SaveId:D}' season {evidence.Target.SeasonNumber} stage {evidence.Target.StageNumber} " +
            $"({evidence.StageTable.Count} athletes, {evidence.Rounds.Count} rounds) in {evidence.ElapsedMs}ms.").ConfigureAwait(false);
        await stdout.WriteAsync(evidence.ToJson()).ConfigureAwait(false);
        await stdout.WriteAsync(Environment.NewLine).ConfigureAwait(false);
        return 0;
    }

    private static Task<int> ArchiveAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("Missing archive subcommand: 'init', 'context' or 'commit'.", nameof(args));
        }

        string subcommand = args[0].Trim().ToLowerInvariant();
        string[] rest = args[1..];
        return subcommand switch
        {
            "init" => ArchiveInitAsync(rest, stdout, stderr),
            "context" => ArchiveContextAsync(rest, stdout, stderr),
            "commit" => ArchiveCommitAsync(rest, stdout, stderr),
            _ => throw new ArgumentException($"Unknown archive subcommand '{args[0]}'. Expected 'init', 'context' or 'commit'.", nameof(args)),
        };
    }

    private static Task<int> ArchiveInitAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string configPath = RequireOption(args, "--config");
        string? archiveOverride = GetOption(args, "--archive");
        SnapshotConfig config = SnapshotConfig.Load(configPath, archiveDirectoryOverride: archiveOverride);
        string archiveRoot = JournalArchive.ResolveArchiveRoot(config, archiveOverride);
        string saveName = TrySnapshotSaveName(config) ?? "Save";
        JournalArchive.SaveFile saved = JournalArchive.Init(archiveRoot, config.SaveId, saveName, DateTimeOffset.UtcNow);
        stderr.WriteLine($"[snapshot] journalist archive ready for save '{saved.SaveId:D}' under '{archiveRoot}'.");
        stdout.Write(JsonSerializer.Serialize(
            new { archiveRoot, saveId = saved.SaveId, saveName = saved.SaveName },
            OutputJsonOptions));
        stdout.Write(Environment.NewLine);
        return Task.FromResult(0);
    }

    private static Task<int> ArchiveContextAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string configPath = RequireOption(args, "--config");
        string? archiveOverride = GetOption(args, "--archive");
        int? season = ParseIntOption(args, "--season");
        int? stage = ParseIntOption(args, "--stage");
        int recent = ParseRecentOption(args);
        SnapshotConfig config = SnapshotConfig.Load(configPath, archiveDirectoryOverride: archiveOverride);
        string archiveRoot = JournalArchive.ResolveArchiveRoot(config, archiveOverride);
        JournalArchive.ContextResult context = JournalArchive.BuildContext(archiveRoot, config.SaveId, season, stage, recent);
        stderr.WriteLine(
            $"[snapshot] archive context for save '{config.SaveId:D}' " +
            (season.HasValue ? $"season {season.Value} stage {stage!.Value} " : "latest ") +
            $"returned {context.Recent.Count} review(s), {context.ExcludedLaterArticles} later review(s) excluded.");
        stdout.Write(JsonSerializer.Serialize(context, OutputJsonOptions));
        stdout.Write(Environment.NewLine);
        return Task.FromResult(0);
    }

    private static Task<int> ArchiveCommitAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        string configPath = RequireOption(args, "--config");
        string? archiveOverride = GetOption(args, "--archive");
        int season = ParseRequiredIntOption(args, "--season");
        int stage = ParseRequiredIntOption(args, "--stage");
        string articlePath = RequireOption(args, "--article");
        string evidencePath = RequireOption(args, "--evidence");
        string? note = GetOption(args, "--note");
        string? noteFile = GetOption(args, "--note-file");
        bool isRevision = HasFlag(args, "--revision");
        if (note is not null && noteFile is not null)
        {
            throw new ArgumentException("Specify only one of '--note' or '--note-file'.", nameof(args));
        }

        if (noteFile is not null)
        {
            note = ReadNoteFile(noteFile);
        }

        SnapshotConfig config = SnapshotConfig.Load(configPath, archiveDirectoryOverride: archiveOverride);
        string archiveRoot = JournalArchive.ResolveArchiveRoot(config, archiveOverride);
        SnapshotInfo? snapshot = TryReadSnapshot(config);
        JournalArchive.MetaFile meta = JournalArchive.Commit(
            archiveRoot, config.SaveId, season, stage, articlePath, evidencePath,
            note, isRevision, DateTimeOffset.UtcNow, snapshot);
        stderr.WriteLine(
            $"[snapshot] archived season {meta.SeasonNumber} stage {meta.StageNumber} rev {meta.Revision} " +
            (meta.Conflict ? "with provenance conflict (prior text preserved in .r<N> backups) " : string.Empty) +
            $"for save '{meta.SaveId:D}'.");
        stdout.Write(JsonSerializer.Serialize(meta, OutputJsonOptions));
        stdout.Write(Environment.NewLine);
        return Task.FromResult(0);
    }

    private static string? TrySnapshotSaveName(SnapshotConfig config)
    {
        try
        {
            return SnapshotReader.ReadInfo(config.WorkingDirectory).SaveName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static SnapshotInfo? TryReadSnapshot(SnapshotConfig config)
    {
        try
        {
            return SnapshotReader.ReadInfo(config.WorkingDirectory);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadNoteFile(string noteFile)
    {
        string fullPath = Path.GetFullPath(noteFile);
        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Note file could not be read: '{fullPath}'.", ex);
        }

        if (text.Length > JournalArchive.MaxNoteChars + 1024)
        {
            throw new ArgumentException($"Note file must be at most {JournalArchive.MaxNoteChars} characters after trimming.", nameof(noteFile));
        }

        return text;
    }

    private static int ParseRecentOption(string[] args)
    {
        string? raw = GetOption(args, "--recent");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return JournalArchive.DefaultRecent;
        }

        if (!int.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out int recent))
        {
            throw new ArgumentException("Option '--recent' must be an integer.", nameof(args));
        }

        return recent;
    }

    private static int ParseRequiredIntOption(string[] args, string name)
    {
        string? raw = GetOption(args, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException($"Missing required option '{name}'.", nameof(args));
        }

        if (!int.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out int value))
        {
            throw new ArgumentException($"Option '{name}' must be an integer.", nameof(args));
        }

        return value;
    }

    private static bool HasFlag(string[] args, string name)
    {
        foreach (string arg in args)
        {
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler? overrideHandler)
    {
        // MSS-136: no download timeout ceiling for large saves. Multi-gigabyte
        // loopback downloads stream to a temp file with bounded buffers;
        // cancellation (Ctrl+C) remains effective during long transfers.
        // List-saves uses the same client; a hung app is surfaced by
        // cancellation, not by a size-implying timeout.
        if (overrideHandler is not null)
        {
            return new HttpClient(overrideHandler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        return new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static void ValidateBundleFile(string fullBundlePath)
    {
        long length;
        try
        {
            FileInfo info = new(fullBundlePath);
            if (!info.Exists)
            {
                throw new InvalidOperationException($"Offline bundle file could not be read: '{fullBundlePath}'.");
            }

            length = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Offline bundle file could not be read: '{fullBundlePath}'.", ex);
        }

        if (length == 0)
        {
            throw new InvalidOperationException($"Offline bundle file is empty: '{fullBundlePath}'.");
        }

        // MSS-136: no fixed bundle size ceiling. Offline input streams from
        // the file with bounded buffers; only disk space and platform limits
        // apply. The small manifest inside stays bounded.
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

    private static int? ParseIntOption(string[] args, string name)
    {
        string? raw = GetOption(args, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!int.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out int value))
        {
            throw new ArgumentException($"Option '{name}' must be an integer.", nameof(args));
        }

        return value;
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
        "MtgSoloSports save snapshot helper (MSS-133/134/135): fetch one verified read-only\n" +
        "SQLite snapshot for the explicitly configured save.\n" +
        "\n" +
        "Usage:\n" +
        "  snapshot list-saves [--config <path>] [--base-url <url>]\n" +
        "  snapshot acquire --config <path> [--bundle <file>] [--base-url <url>] [--save-id <guid>] [--working-dir <path>]\n" +
        "  snapshot info --config <path> [--working-dir <path>]\n" +
        "  snapshot query --config <path> --sql \"<SELECT ...>\" [--limit <n>] [--working-dir <path>]\n" +
        "  snapshot evidence --config <path> [--season <n>] [--stage <n>] [--athlete <name>] [--athlete-id <id>] [--working-dir <path>]\n" +
        "  snapshot archive init --config <path> [--archive <dir>]\n" +
        "  snapshot archive context --config <path> [--season <n> --stage <n>] [--recent <n>] [--archive <dir>]\n" +
        "  snapshot archive commit --config <path> --season <n> --stage <n> --article <file.md> --evidence <file.json> [--note <text> | --note-file <file>] [--revision] [--archive <dir>]\n" +
        "\n" +
        "The loopback game app is the only network access (GET /api/saves and\n" +
        "GET /api/saves/{id}/export); --bundle works fully offline. Inspection is\n" +
        "read-only (Mode=ReadOnly + query_only) and never migrates or simulates.\n" +
        "Evidence (MSS-134) reads the verified snapshot only: latest completed\n" +
        "Superleague stage by default, or an explicit --season/--stage cutoff with\n" +
        "optional bounded --athlete follow-up. Archive (MSS-135) is the local\n" +
        "journalist store: 'init' creates save-ID scoped directories, 'context'\n" +
        "returns bounded earlier coverage with future-outcome exclusion for an\n" +
        "explicit cutoff, and 'commit' saves the article with matching evidence\n" +
        "provenance (explicit '--revision' preserves prior text). Data JSON goes to stdout, logs go to stderr.\n";
}
