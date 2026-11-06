using System.IO.Compression;
using System.Security.Cryptography;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Portable save artifact helpers. A bundle is a ZIP containing exactly two
/// entries: <c>save.db</c> (the authoritative SQLite save database; the
/// external artwork cache is intentionally not embedded) and
/// <c>manifest.json</c> (version metadata). Entry names are fixed and output
/// paths are always staging-controlled, so extraction can never escape the
/// staging directory (no Zip Slip).
/// </summary>
public static class SaveBundle
{
    /// <summary>
    /// Upper bound for an ordinary game import bundle. Large enough for
    /// long-run saves, small enough to keep import memory-bounded on
    /// localhost. The local snapshot workflow (MSS-136) does not use this
    /// limit: snapshot acquisition streams through temporary files with no
    /// fixed bundle/database ceiling, subject only to disk space and
    /// platform limits.
    /// </summary>
    public const long MaxBundleBytes = 256L * 1024L * 1024L;

    /// <summary>
    /// Upper bound for the embedded database payload on the ordinary game
    /// import path. Rejects zip bombs before extraction commits disk
    /// outside staging. Snapshot acquisition (MSS-136) streams without this
    /// ceiling; only the small manifest stays bounded there.
    /// </summary>
    public const long MaxDatabaseBytes = 256L * 1024L * 1024L;

    /// <summary>
    /// Upper bound for the embedded manifest payload.
    /// </summary>
    public const long MaxManifestBytes = 1L * 1024L * 1024L;

    /// <summary>
    /// Builds a portable bundle from a save database file plus its manifest,
    /// streaming through bounded buffers into a caller-owned destination file
    /// without ever holding the whole ZIP or database in memory. Wide file
    /// streams impose no 2 GiB array ceiling; multi-gigabyte saves are
    /// constrained only by disk space and platform limits.
    /// Source reads allow concurrent pooled SQLite handles (Windows share mode).
    /// </summary>
    public static async Task CreateToFileAsync(
        string saveDbPath,
        SaveBundleManifest manifest,
        string bundlePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDbPath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        if (!File.Exists(saveDbPath))
        {
            throw new FileNotFoundException("Save database file was not found.", saveDbPath);
        }

        manifest.ValidateForExport();

        string? directory = Path.GetDirectoryName(Path.GetFullPath(bundlePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            using FileStream destination = new(
                bundlePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.SequentialScan);
            using (ZipArchive archive = new(destination, ZipArchiveMode.Create, leaveOpen: false))
            {
                ZipArchiveEntry databaseEntry = archive.CreateEntry(
                    SaveBundleManifest.DatabaseEntryName, CompressionLevel.Optimal);
                using (Stream entryStream = databaseEntry.Open())
                using (FileStream source = OpenSharedRead(saveDbPath))
                {
                    await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
                }

                byte[] manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest.ToJson());
                ZipArchiveEntry manifestEntry = archive.CreateEntry(
                    SaveBundleManifest.ManifestEntryName, CompressionLevel.Optimal);
                using (Stream entryStream = manifestEntry.Open())
                {
                    await entryStream.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteFileBestEffort(bundlePath);
            if (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException)
            {
                throw;
            }

            throw new InvalidOperationException(
                $"Save bundle could not be written to '{bundlePath}'.", ex);
        }
    }

    /// <summary>
    /// Builds a portable bundle from a live save database file plus its manifest.
    /// Source reads allow concurrent pooled SQLite handles (Windows share mode).
    /// Retained for ordinary small-save import/export compatibility; the
    /// production snapshot path uses <see cref="CreateToFileAsync"/> to avoid
    /// whole-artifact byte arrays.
    /// </summary>
    public static byte[] Create(string saveDbPath, SaveBundleManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveDbPath);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!File.Exists(saveDbPath))
        {
            throw new FileNotFoundException("Save database file was not found.", saveDbPath);
        }

        manifest.ValidateForExport();

        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry databaseEntry = archive.CreateEntry(
                SaveBundleManifest.DatabaseEntryName, CompressionLevel.Optimal);
            using (Stream entryStream = databaseEntry.Open())
            using (FileStream source = OpenSharedRead(saveDbPath))
            {
                source.CopyTo(entryStream);
            }

            byte[] manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifest.ToJson());
            ZipArchiveEntry manifestEntry = archive.CreateEntry(
                SaveBundleManifest.ManifestEntryName, CompressionLevel.Optimal);
            using (Stream entryStream = manifestEntry.Open())
            {
                entryStream.Write(manifestBytes, 0, manifestBytes.Length);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads an upload body with a hard cap so oversized payloads fail fast
    /// without buffering unbounded memory.
    /// </summary>
    public static async Task<byte[]> ReadCappedAsync(Stream body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total = checked(total + read);
            if (total > MaxBundleBytes)
            {
                throw new InvalidOperationException(
                    $"Save bundle exceeds the {MaxBundleBytes} byte import limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        byte[] bytes = buffer.ToArray();
        if (bytes.Length == 0)
        {
            throw new ArgumentException("Save bundle body is required.", nameof(body));
        }

        return bytes;
    }

    /// <summary>
    /// Validates archive shape and extracts the database payload plus manifest
    /// into a caller-owned staging directory. Returns the staged database path
    /// and the parsed manifest. Throws without extracting anything usable when
    /// the archive is malformed or carries unexpected entries.
    /// </summary>
    public sealed record Extraction(string StagedDatabasePath, SaveBundleManifest Manifest);

    public static async Task<Extraction> ExtractToStagingAsync(
        byte[] bundleBytes,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundleBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        if (bundleBytes.Length == 0)
        {
            throw new ArgumentException("Save bundle is empty.", nameof(bundleBytes));
        }

        if (bundleBytes.LongLength > MaxBundleBytes)
        {
            throw new InvalidOperationException(
                $"Save bundle exceeds the {MaxBundleBytes} byte import limit.");
        }

        Directory.CreateDirectory(stagingDirectory);
        string stagedDatabasePath = Path.Combine(stagingDirectory, SaveBundleManifest.DatabaseEntryName);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(bundleBytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("Uploaded file is not a valid save bundle (not a ZIP archive).", ex);
        }

        using (archive)
        {
            (ZipArchiveEntry databaseEntry, ZipArchiveEntry manifestEntry) = FindBundleEntries(archive);
            EnsureEntrySizes(databaseEntry, manifestEntry);
            SaveBundleManifest manifest = await ReadManifestAsync(manifestEntry, cancellationToken).ConfigureAwait(false);
            await WriteDatabaseEntryAsync(databaseEntry, stagedDatabasePath, cancellationToken).ConfigureAwait(false);

            string actualChecksum = ComputeFileSha256Hex(stagedDatabasePath);
            if (!string.Equals(actualChecksum, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Save bundle database checksum does not match the manifest. The artifact is corrupt or tampered with.");
            }

            return new Extraction(stagedDatabasePath, manifest);
        }
    }

    private static (ZipArchiveEntry Database, ZipArchiveEntry Manifest) FindBundleEntries(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        IReadOnlyList<ZipArchiveEntry> entries = archive.Entries;
        if (entries.Count != 2)
        {
            throw new InvalidOperationException(
                $"Save bundle must contain exactly 2 entries (save.db and manifest.json), was {entries.Count}.");
        }

        ZipArchiveEntry? databaseEntry = null;
        ZipArchiveEntry? manifestEntry = null;
        foreach (ZipArchiveEntry entry in entries)
        {
            if (string.Equals(entry.FullName, SaveBundleManifest.DatabaseEntryName, StringComparison.Ordinal))
            {
                databaseEntry = entry;
            }
            else if (string.Equals(entry.FullName, SaveBundleManifest.ManifestEntryName, StringComparison.Ordinal))
            {
                manifestEntry = entry;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Save bundle contains unexpected entry '{entry.FullName}'.");
            }
        }

        if (databaseEntry is null || manifestEntry is null)
        {
            throw new InvalidOperationException(
                "Save bundle must contain save.db and manifest.json entries.");
        }

        return (databaseEntry, manifestEntry);
    }

    /// <summary>
    /// Validates archive shape and extracts the database payload plus manifest
    /// from a bundle file into a caller-owned staging directory, streaming
    /// with bounded buffers and no fixed bundle/database ceiling. Only the
    /// small manifest stays bounded. Used by the local snapshot workflow
    /// (MSS-136) so multi-gigabyte saves work subject only to disk space and
    /// platform limits. Entry names stay fixed and output paths stay
    /// staging-controlled (no Zip Slip); checksum, identity and
    /// schema/rules/RNG checks still apply at the caller.
    /// </summary>
    public static async Task<Extraction> ExtractFileToStagingAsync(
        string bundlePath,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        EnsureBundleFileNotEmpty(bundlePath);

        Directory.CreateDirectory(stagingDirectory);
        string stagedDatabasePath = Path.Combine(stagingDirectory, SaveBundleManifest.DatabaseEntryName);

        FileStream bundleStream = OpenBundleFile(bundlePath);
        try
        {
            return await ExtractArchiveToStagingAsync(bundleStream, stagedDatabasePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteFileBestEffort(stagedDatabasePath);
            if (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                throw;
            }

            throw new InvalidOperationException(
                "Save bundle could not be extracted.", ex);
        }
    }

    private static void EnsureBundleFileNotEmpty(string bundlePath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(bundlePath);
            if (!info.Exists)
            {
                throw new FileNotFoundException("Save bundle file was not found.", bundlePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Save bundle file could not be read: '{bundlePath}'.", ex);
        }

        if (info.Length == 0)
        {
            throw new InvalidOperationException("Save bundle is empty. The download did not produce any bytes.");
        }
    }

    private static FileStream OpenBundleFile(string bundlePath)
    {
        try
        {
            return new FileStream(
                bundlePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Save bundle file could not be read: '{bundlePath}'.", ex);
        }
    }

    private static async Task<Extraction> ExtractArchiveToStagingAsync(
        FileStream bundleStream,
        string stagedDatabasePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundleStream);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(bundleStream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            bundleStream.Dispose();
            throw new InvalidOperationException("Uploaded file is not a valid save bundle (not a ZIP archive).", ex);
        }

        using (archive)
        {
            (ZipArchiveEntry databaseEntry, ZipArchiveEntry manifestEntry) = FindBundleEntries(archive);
            EnsureManifestSize(manifestEntry);
            SaveBundleManifest manifest = await ReadManifestCappedAsync(manifestEntry, cancellationToken).ConfigureAwait(false);
            await WriteDatabaseEntryAsync(databaseEntry, stagedDatabasePath, cancellationToken).ConfigureAwait(false);
            EnsureChecksumMatches(stagedDatabasePath, manifest);
            return new Extraction(stagedDatabasePath, manifest);
        }
    }

    private static void EnsureChecksumMatches(string stagedDatabasePath, SaveBundleManifest manifest)
    {
        string actualChecksum = ComputeFileSha256Hex(stagedDatabasePath);
        if (!string.Equals(actualChecksum, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Save bundle database checksum does not match the manifest. The artifact is corrupt or tampered with.");
        }
    }

    private static void EnsureEntrySizes(ZipArchiveEntry databaseEntry, ZipArchiveEntry manifestEntry)
    {
        ArgumentNullException.ThrowIfNull(databaseEntry);
        ArgumentNullException.ThrowIfNull(manifestEntry);
        if (databaseEntry.Length > MaxDatabaseBytes || databaseEntry.Length < 0)
        {
            throw new InvalidOperationException("Save bundle database payload has an unsupported size.");
        }

        EnsureManifestSize(manifestEntry);
    }

    private static void EnsureManifestSize(ZipArchiveEntry manifestEntry)
    {
        ArgumentNullException.ThrowIfNull(manifestEntry);
        if (manifestEntry.Length > MaxManifestBytes || manifestEntry.Length <= 0)
        {
            throw new InvalidOperationException("Save bundle manifest payload has an unsupported size.");
        }
    }

    private static async Task<SaveBundleManifest> ReadManifestAsync(
        ZipArchiveEntry manifestEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestEntry);
        string manifestJson = await ReadManifestTextAsync(manifestEntry, cancellationToken).ConfigureAwait(false);

        SaveBundleManifest manifest = SaveBundleManifest.FromJson(manifestJson);
        manifest.ValidateForImport();
        return manifest;
    }

    private static Task<SaveBundleManifest> ReadManifestCappedAsync(
        ZipArchiveEntry manifestEntry,
        CancellationToken cancellationToken)
    {
        // Same manifest validation as the import path; only the small manifest
        // stays bounded on the uncapped snapshot path.
        return ReadManifestAsync(manifestEntry, cancellationToken);
    }

    private static async Task<string> ReadManifestTextAsync(
        ZipArchiveEntry manifestEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifestEntry);
        using Stream manifestStream = manifestEntry.Open();
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await manifestStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total = checked(total + read);
            if (total > MaxManifestBytes)
            {
                throw new InvalidOperationException("Save bundle manifest payload has an unsupported size.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            throw new InvalidOperationException("Save bundle manifest payload has an unsupported size.");
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task WriteDatabaseEntryAsync(
        ZipArchiveEntry databaseEntry,
        string stagedDatabasePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(databaseEntry);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDatabasePath);
        using (Stream databaseStream = databaseEntry.Open())
        using (FileStream target = File.Create(stagedDatabasePath))
        {
            await databaseStream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    public static string ComputeFileSha256Hex(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = OpenSharedRead(path);
        return ComputeStreamSha256Hex(stream);
    }

    private static FileStream OpenSharedRead(string path)
    {
        // Pooled SQLite handles keep the database file open for writes; a plain
        // File.OpenRead (Share.Read) throws a Windows sharing violation while
        // those handles exist. Allow concurrent readers/writers so hashing and
        // bundling work after ordinary disposed contexts without clearing pools
        // before the read. The snapshot gate still serializes replacements.
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.SequentialScan);
    }

    public static string ComputeBytesSha256Hex(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using MemoryStream stream = new(bytes, writable: false);
        return ComputeStreamSha256Hex(stream);
    }

    private static string ComputeStreamSha256Hex(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
