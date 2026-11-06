using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MtgSoloSports.Features.Saves;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.SaveSnapshot;

/// <summary>
/// Acquires a verified working snapshot for one explicitly configured save.
/// The only game access is the existing loopback export endpoint
/// (<c>GET /api/saves/{id}/export</c>); an already downloaded bundle file can
/// be used offline instead. Every failure mode rejects the artifact before
/// anything is published, so a failed download or validation never replaces
/// the previous valid snapshot or represents it as fresh.
/// MSS-136: acquisition streams through temporary files with bounded buffers
/// and no fixed bundle/database ceiling, subject only to disk space and
/// platform limits. Only the small manifest stays bounded.
/// </summary>
public static class SnapshotAcquirer
{
    /// <summary>
    /// Validates a bundle file and publishes the new working snapshot atomically,
    /// streaming with bounded buffers and no fixed size ceiling. This is the
    /// production snapshot path for both HTTP downloads (via a temp file) and
    /// offline <c>--bundle</c> input.
    /// </summary>
    public static async Task<SnapshotInfo> AcquireFromBundleFileAsync(
        string bundlePath,
        Guid expectedSaveId,
        string workingDirectory,
        DateTimeOffset acquiredUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        if (expectedSaveId == Guid.Empty)
        {
            throw new ArgumentException("Expected save id must not be empty.", nameof(expectedSaveId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!File.Exists(bundlePath))
        {
            throw new InvalidOperationException($"Offline bundle file could not be read: '{Path.GetFullPath(bundlePath)}'.");
        }

        string root = Path.GetFullPath(workingDirectory);
        Directory.CreateDirectory(root);
        string stagingDirectory = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            return await ValidateAndPublishAsync(
                bundlePath, expectedSaveId, root, stagingDirectory, acquiredUtc, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDirectoryBestEffort(stagingDirectory);
        }
    }

    /// <summary>
    /// Validates bundle bytes and publishes the new working snapshot atomically.
    /// Retained for small-save compatibility; delegates to the file-streaming
    /// path so extraction itself never holds the database in memory. Large
    /// saves must use <see cref="AcquireFromBundleFileAsync"/> (or HTTP
    /// acquisition) to avoid holding the whole ZIP in a byte array.
    /// </summary>
    public static async Task<SnapshotInfo> AcquireFromBundleAsync(
        byte[] bundleBytes,
        Guid expectedSaveId,
        string workingDirectory,
        DateTimeOffset acquiredUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundleBytes);
        if (expectedSaveId == Guid.Empty)
        {
            throw new ArgumentException("Expected save id must not be empty.", nameof(expectedSaveId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (bundleBytes.Length == 0)
        {
            throw new InvalidOperationException("Save bundle is empty. The download did not produce any bytes.");
        }

        string tempBundlePath = Path.Combine(
            Path.GetTempPath(),
            ".mtgsolosports-bundle-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(tempBundlePath, bundleBytes, cancellationToken).ConfigureAwait(false);
            return await AcquireFromBundleFileAsync(
                tempBundlePath, expectedSaveId, workingDirectory, acquiredUtc, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteFileBestEffort(tempBundlePath);
        }
    }

    private static async Task<SnapshotInfo> ValidateAndPublishAsync(
        string bundlePath,
        Guid expectedSaveId,
        string root,
        string stagingDirectory,
        DateTimeOffset acquiredUtc,
        CancellationToken cancellationToken)
    {
        SaveBundle.Extraction extraction = await SaveBundle
            .ExtractFileToStagingAsync(bundlePath, stagingDirectory, cancellationToken)
            .ConfigureAwait(false);
        SaveBundleManifest manifest = ValidateManifestIdentity(extraction.Manifest, expectedSaveId);
        SaveRulesCompatibility.EnsureImportableRules(manifest);
        SnapshotReader.DatabaseView view = SnapshotReader.ReadDatabase(extraction.StagedDatabasePath);
        ValidateDatabaseMatchesManifest(view, manifest);

        // Proves the persisted sporting snapshot still parses; never migrates or repairs it.
        _ = SaveRulesCompatibility.ReadSnapshotRules(view.RulesJson);

        SnapshotInfo info = BuildInfo(manifest, view, root, acquiredUtc);
        PublishAtomically(extraction.StagedDatabasePath, manifest, info, root);
        return info;
    }

    private static SaveBundleManifest ValidateManifestIdentity(SaveBundleManifest manifest, Guid expectedSaveId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.SaveId.Equals(expectedSaveId))
        {
            throw new InvalidOperationException(
                $"Save bundle identity '{manifest.SaveId:D}' does not match the configured save id '{expectedSaveId:D}'. " +
                "Run 'list-saves' and configure the exact save id; the helper never falls back to another save.");
        }

        return manifest;
    }

    private static void ValidateDatabaseMatchesManifest(SnapshotReader.DatabaseView view, SaveBundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!view.SaveId.Equals(manifest.SaveId))
        {
            throw new InvalidOperationException(
                $"Snapshot database identity '{view.SaveId:D}' does not match manifest save '{manifest.SaveId:D}'.");
        }

        if (view.SchemaVersion != manifest.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Snapshot database schema version {view.SchemaVersion} does not match manifest version {manifest.SchemaVersion}.");
        }

        if (view.RulesVersion != manifest.RulesVersion)
        {
            throw new InvalidOperationException(
                $"Snapshot database rules version {view.RulesVersion} does not match manifest version {manifest.RulesVersion}.");
        }

        if (!string.Equals(view.RngAlgorithm, manifest.RngAlgorithm, StringComparison.Ordinal)
            || view.RngVersion != manifest.RngVersion)
        {
            throw new InvalidOperationException(
                $"Snapshot database RNG '{view.RngAlgorithm}' v{view.RngVersion} does not match manifest RNG " +
                $"'{manifest.RngAlgorithm}' v{manifest.RngVersion}.");
        }
    }

    private static SnapshotInfo BuildInfo(
        SaveBundleManifest manifest,
        SnapshotReader.DatabaseView view,
        string root,
        DateTimeOffset acquiredUtc)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(view);
        return new SnapshotInfo(
            SnapshotReader.DatabasePathFor(root),
            manifest.SaveId,
            manifest.Name,
            manifest.ExportedUtc,
            acquiredUtc,
            manifest.SchemaVersion,
            manifest.RulesVersion,
            manifest.RngAlgorithm,
            manifest.RngVersion,
            manifest.DatabaseSha256,
            manifest.CurrentSeason,
            manifest.Phase,
            view.Seasons);
    }

    /// <summary>
    /// Downloads the export for the configured save and publishes the snapshot,
    /// streaming the HTTP body to a temporary file with bounded buffers and no
    /// fixed size ceiling. Cancellation remains effective during long
    /// transfers; there is no download timeout ceiling (the HttpClient uses an
    /// infinite timeout, documented in the snapshot guide). A failed,
    /// truncated, or cancelled download never replaces a previous snapshot and
    /// always cleans its temp file.
    /// </summary>
    public static async Task<SnapshotInfo> AcquireFromHttpAsync(
        HttpClient httpClient,
        string baseUrl,
        Guid expectedSaveId,
        string workingDirectory,
        DateTimeOffset acquiredUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        if (expectedSaveId == Guid.Empty)
        {
            throw new ArgumentException("Expected save id must not be empty.", nameof(expectedSaveId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string downloadPath = Path.Combine(
            Path.GetTempPath(),
            ".mtgsolosports-download-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await DownloadBundleToFileAsync(httpClient, baseUrl, expectedSaveId, downloadPath, cancellationToken).ConfigureAwait(false);
            return await AcquireFromBundleFileAsync(
                downloadPath, expectedSaveId, workingDirectory, acquiredUtc, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteFileBestEffort(downloadPath);
        }
    }

    /// <summary>
    /// Fetches the existing export artifact to a caller-owned file, streaming
    /// with bounded buffers, no fixed size ceiling, and no reliance on
    /// Content-Length. Retained byte-array overload delegates to the file path
    /// for small-save compatibility only; production acquisition uses
    /// <see cref="DownloadBundleToFileAsync"/>.
    /// </summary>
    public static async Task<byte[]> DownloadBundleAsync(
        HttpClient httpClient,
        string baseUrl,
        Guid saveId,
        CancellationToken cancellationToken = default)
    {
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            ".mtgsolosports-download-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await DownloadBundleToFileAsync(httpClient, baseUrl, saveId, tempPath, cancellationToken).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(tempPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteFileBestEffort(tempPath);
        }
    }

    /// <summary>
    /// Streams the export artifact to <paramref name="destinationPath"/> with
    /// bounded buffers and no fixed size ceiling. Does not rely on
    /// Content-Length; unknown or chunked lengths stream until EOF.
    /// </summary>
    public static async Task DownloadBundleToFileAsync(
        HttpClient httpClient,
        string baseUrl,
        Guid saveId,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string baseRoot = baseUrl.Trim().TrimEnd('/');
        string requestUri = $"{baseRoot}/api/saves/{saveId:D}/export";
        HttpResponseMessage response = await GetExportResponseAsync(httpClient, requestUri, baseRoot, cancellationToken).ConfigureAwait(false);

        using (response)
        {
            EnsureExportSuccess(response, saveId, baseRoot);
            await StreamResponseToFileAsync(response, saveId, destinationPath, cancellationToken).ConfigureAwait(false);
            EnsureDownloadedNotEmpty(destinationPath);
        }
    }

    private static async Task<HttpResponseMessage> GetExportResponseAsync(
        HttpClient httpClient,
        string requestUri,
        string baseRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Game app at '{baseRoot}' is unavailable ({ex.Message}). " +
                "Start the default container with 'docker compose up --build' and retry.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Game app at '{baseRoot}' did not answer in time ({ex.Message}). " +
                "Start the default container with 'docker compose up --build' and retry.", ex);
        }
    }

    private static void EnsureExportSuccess(HttpResponseMessage response, Guid saveId, string baseRoot)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"Save '{saveId:D}' was not found on the game app at '{baseRoot}'. " +
                "Run 'list-saves' to pick the exact save id.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Game app export failed with {(int)response.StatusCode} ({response.ReasonPhrase}) " +
                $"for save '{saveId:D}' at '{baseRoot}'.");
        }
    }

    private static async Task StreamResponseToFileAsync(
        HttpResponseMessage response,
        Guid saveId,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using FileStream target = new(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.SequentialScan);
            await CopyStreamAsync(source, target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteFileBestEffort(destinationPath);
            if (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                throw;
            }

            throw new InvalidOperationException(
                $"Game app export download failed for save '{saveId:D}'.", ex);
        }
    }

    private static void EnsureDownloadedNotEmpty(string destinationPath)
    {
        FileInfo downloaded = new(destinationPath);
        if (!downloaded.Exists || downloaded.Length == 0)
        {
            DeleteFileBestEffort(destinationPath);
            throw new InvalidOperationException("Game app export returned an empty bundle.");
        }
    }

    /// <summary>
    /// Enumerates saves for one-time setup. Callers must still configure the
    /// exact save id explicitly; this helper never selects one automatically.
    /// </summary>
    public static async Task<IReadOnlyList<SaveSummary>> ListSavesAsync(
        HttpClient httpClient,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        string baseRoot = baseUrl.Trim().TrimEnd('/');
        string requestUri = $"{baseRoot}/api/saves";
        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Game app at '{baseRoot}' is unavailable ({ex.Message}). " +
                "Start the default container with 'docker compose up --build' and retry.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Game app save listing failed with {(int)response.StatusCode} ({response.ReasonPhrase}) at '{baseRoot}'.");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SaveListDocument? document = JsonSerializer.Deserialize<SaveListDocument>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (document?.Saves is null)
                {
                    throw new InvalidOperationException("Game app save listing payload is empty.");
                }

                return document.Saves;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Game app save listing payload is not valid JSON.", ex);
            }
        }
    }

    private static void PublishAtomically(
        string stagedDatabasePath,
        SaveBundleManifest manifest,
        SnapshotInfo info,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDatabasePath);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string databaseFinal = SnapshotReader.DatabasePathFor(workingDirectory);
        string manifestFinal = SnapshotReader.ManifestPathFor(workingDirectory);
        string infoFinal = SnapshotReader.InfoPathFor(workingDirectory);
        string databaseNew = databaseFinal + ".new";
        string manifestNew = manifestFinal + ".new";
        string infoNew = infoFinal + ".new";
        try
        {
            // Refresh replaces the database file, so stale WAL bookkeeping for
            // the previous snapshot is dropped first; reads recreate it empty.
            DeleteFileBestEffort(databaseFinal + "-wal");
            DeleteFileBestEffort(databaseFinal + "-shm");
            DeleteFileBestEffort(databaseFinal + "-journal");
            File.Copy(stagedDatabasePath, databaseNew, overwrite: true);
            File.WriteAllText(manifestNew, manifest.ToJson());
            File.WriteAllText(infoNew, info.ToJson());

            // Metadata last: a torn publish fails the ReadInfo hash check
            // instead of serving a half-replaced snapshot as current.
            File.Move(databaseNew, databaseFinal, overwrite: true);
            File.Move(manifestNew, manifestFinal, overwrite: true);
            File.Move(infoNew, infoFinal, overwrite: true);
        }
        catch
        {
            DeleteFileBestEffort(databaseNew);
            DeleteFileBestEffort(manifestNew);
            DeleteFileBestEffort(infoNew);
            throw;
        }
    }

    private static async Task CopyStreamAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Staging cleanup is best effort; a leftover staging directory
            // never affects the published working snapshot.
        }
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

    private sealed record SaveListDocument(IReadOnlyList<SaveSummary>? Saves);
}
