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
    /// Upper bound for an uploaded bundle. Large enough for long-run saves,
    /// small enough to keep import memory-bounded on localhost.
    /// </summary>
    public const long MaxBundleBytes = 256L * 1024L * 1024L;

    /// <summary>
    /// Upper bound for the embedded database payload. Rejects zip bombs
    /// before extraction commits disk outside staging.
    /// </summary>
    public const long MaxDatabaseBytes = 256L * 1024L * 1024L;

    /// <summary>
    /// Upper bound for the embedded manifest payload.
    /// </summary>
    public const long MaxManifestBytes = 1L * 1024L * 1024L;

    /// <summary>
    /// Builds a portable bundle from a live save database file plus its manifest.
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
            using (FileStream source = File.OpenRead(saveDbPath))
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

    private static void EnsureEntrySizes(ZipArchiveEntry databaseEntry, ZipArchiveEntry manifestEntry)
    {
        ArgumentNullException.ThrowIfNull(databaseEntry);
        ArgumentNullException.ThrowIfNull(manifestEntry);
        if (databaseEntry.Length > MaxDatabaseBytes || databaseEntry.Length < 0)
        {
            throw new InvalidOperationException("Save bundle database payload has an unsupported size.");
        }

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
        string manifestJson;
        using (Stream manifestStream = manifestEntry.Open())
        using (StreamReader reader = new(manifestStream, System.Text.Encoding.UTF8))
        {
            manifestJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        SaveBundleManifest manifest = SaveBundleManifest.FromJson(manifestJson);
        manifest.ValidateForImport();
        return manifest;
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
        using FileStream stream = File.OpenRead(path);
        return ComputeStreamSha256Hex(stream);
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
}
