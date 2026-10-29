using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MtgSoloSports.Features.Reports.GoogleDrivePublishing;

/// <summary>
/// File-backed app-level publishing state. Separate from sporting saves and
/// backup ZIPs: one JSON connection file plus one JSON destination file per
/// save under the persistent <see cref="DrivePublishingOptions.StorePath"/>
/// directory. Files are written with owner-only permissions where the OS
/// supports it. Never stores sporting results; only OAuth tokens, Drive file
/// ids/names and last-published fingerprints.
/// </summary>
public sealed class DrivePublishingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IOptions<DrivePublishingOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DrivePublishingStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DrivePublishingStore(
        IOptions<DrivePublishingOptions> options,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<DrivePublishingStore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public sealed record ConnectionDocument(
        string RefreshToken,
        string? Email,
        string Scopes,
        DateTimeOffset ConnectedUtc,
        string? AccessToken,
        DateTimeOffset? AccessTokenExpiresUtc);

    public sealed record DestinationDocument(
        Guid SaveId,
        string SaveName,
        string? MtshistoryFolderId,
        string? MtshistoryFolderName,
        string? SeasonsFolderId,
        string? DossierFileId,
        string? DossierFileName,
        string? AlmanacFileId,
        string? AlmanacFileName,
        string? DossierFingerprint,
        string? AlmanacFingerprint,
        string? DossierRemoteModifiedTime,
        string? AlmanacRemoteModifiedTime,
        DateTimeOffset? LastPublishedUtc,
        int? LastSeasonNumber,
        string? LastPhase,
        int? LastGlobalStage,
        string? LastDossierOutcome,
        string? LastAlmanacOutcome,
        string? LastError);

    public sealed record PendingAuthDocument(
        string State,
        Guid SaveId,
        string CodeVerifier,
        DateTimeOffset CreatedUtc,
        DateTimeOffset ExpiresUtc);

    public string GetStoreRoot() => _options.Value.ResolveStoreRoot(_environment);

    public async Task<ConnectionDocument?> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(GetStoreRoot(), "connection.json");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadJsonAsync<ConnectionDocument>(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveConnectionAsync(ConnectionDocument connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        string root = GetStoreRoot();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteJsonAsync(Path.Combine(root, "connection.json"), connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearConnectionAsync(CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(GetStoreRoot(), "connection.json");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeleteFileBestEffort(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DestinationDocument?> GetDestinationAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        string path = DestinationPath(saveId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadJsonAsync<DestinationDocument>(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveDestinationAsync(DestinationDocument destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string root = GetStoreRoot();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "destinations"));
            await WriteJsonAsync(DestinationPath(destination.SaveId), destination, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DestinationDocument>> ListDestinationsAsync(CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(GetStoreRoot(), "destinations");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DestinationDocument> result = [];
            if (!Directory.Exists(directory))
            {
                return result;
            }

            foreach (string file in Directory.GetFiles(directory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    DestinationDocument? document =
                        JsonSerializer.Deserialize<DestinationDocument>(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
                    if (document is not null && document.SaveId != Guid.Empty)
                    {
                        result.Add(document);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _logger.LogWarning(ex, "Skipping unreadable Drive destination file {File}.", file);
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePendingAsync(PendingAuthDocument pending, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pending);
        string root = GetStoreRoot();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "pending"));
            PruneExpiredPendingsLocked(DateTimeOffset.UtcNow);
            await WriteJsonAsync(Path.Combine(root, "pending", pending.State + ".json"), pending, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Single-use CSRF state consumption: invalid, expired or already-used states return null.</summary>
    public async Task<PendingAuthDocument?> TakePendingAsync(string state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        string path = Path.Combine(GetStoreRoot(), "pending", state + ".json");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingAuthDocument? pending = await ReadJsonAsync<PendingAuthDocument>(path, cancellationToken).ConfigureAwait(false);
            DeleteFileBestEffort(path);
            if (pending is null)
            {
                return null;
            }

            if (!string.Equals(pending.State, state, StringComparison.Ordinal))
            {
                return null;
            }

            if (pending.ExpiresUtc <= _timeProvider.GetUtcNow())
            {
                return null;
            }

            return pending;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string DestinationPath(Guid saveId) =>
        Path.Combine(GetStoreRoot(), "destinations", $"{saveId:N}.json");

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string text = JsonSerializer.Serialize(value, JsonOptions);
        string temp = path + $".tmp-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(temp, text, cancellationToken).ConfigureAwait(false);
        RestrictFileBestEffort(temp);
        File.Move(temp, path, overwrite: true);
        RestrictFileBestEffort(path);
    }

    private void PruneExpiredPendingsLocked(DateTimeOffset now)
    {
        string directory;
        try
        {
            directory = Path.Combine(GetStoreRoot(), "pending");
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (string file in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    PendingAuthDocument? pending =
                        JsonSerializer.Deserialize<PendingAuthDocument>(File.ReadAllText(file));
                    if (pending is null || pending.ExpiresUtc <= now)
                    {
                        DeleteFileBestEffort(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    DeleteFileBestEffort(file);
                    _logger.LogWarning(ex, "Removed unreadable Drive pending-auth file {File}.", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not prune Drive pending-auth states.");
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

    private static void RestrictFileBestEffort(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }
}
