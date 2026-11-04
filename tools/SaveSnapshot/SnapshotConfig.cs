using System.Text.Json;

namespace MtgSoloSports.SaveSnapshot;

/// <summary>
/// One-time local configuration for the snapshot helper: the loopback app URL,
/// the explicitly chosen save id, and the local working directory.
/// Relative working directories resolve against the config file directory.
/// The helper never silently selects the first save or infers identity from a
/// mutable display name: acquisition always requires this explicit save id.
/// </summary>
public sealed record SnapshotConfig(string BaseUrl, Guid SaveId, string WorkingDirectory)
{
    public const string ExampleBaseUrl = "http://localhost:8080";

    /// <summary>
    /// Loads and validates a config file, applying explicit CLI overrides.
    /// </summary>
    public static SnapshotConfig Load(
        string configPath,
        string? baseUrlOverride = null,
        Guid? saveIdOverride = null,
        string? workingDirectoryOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        string fullConfigPath = Path.GetFullPath(configPath);
        RawConfig raw = ReadRaw(fullConfigPath, baseUrlOverride, saveIdOverride, workingDirectoryOverride);
        return Build(fullConfigPath, raw);
    }

    private sealed record RawConfig(string? BaseUrl, Guid? SaveId, string? WorkingDirectory);

    private static RawConfig ReadRaw(
        string fullConfigPath,
        string? baseUrlOverride,
        Guid? saveIdOverride,
        string? workingDirectoryOverride)
    {
        if (!File.Exists(fullConfigPath))
        {
            throw new InvalidOperationException(
                $"Snapshot config file was not found: '{fullConfigPath}'. " +
                "Copy 'snapshot.config.example.json' to 'snapshot.config.json' and set the explicit save id.");
        }

        string json;
        try
        {
            json = File.ReadAllText(fullConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Snapshot config file could not be read: '{fullConfigPath}'.", ex);
        }

        string? baseUrl = baseUrlOverride;
        Guid? saveId = saveIdOverride;
        string? workingDirectory = workingDirectoryOverride;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            baseUrl ??= GetOptionalString(root, "baseUrl");
            workingDirectory ??= GetOptionalString(root, "workingDirectory");
            saveId ??= GetOptionalSaveId(root);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Snapshot config file is not valid JSON: '{fullConfigPath}'.", ex);
        }

        return new RawConfig(baseUrl, saveId, workingDirectory);
    }

    private static SnapshotConfig Build(string fullConfigPath, RawConfig raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (string.IsNullOrWhiteSpace(raw.BaseUrl))
        {
            throw new InvalidOperationException(
                "Snapshot config 'baseUrl' is required (for example \"http://localhost:8080\").");
        }

        string baseUrl = raw.BaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Snapshot config 'baseUrl' must be an absolute http(s) URL, was '{baseUrl}'.");
        }

        if (raw.SaveId is null || raw.SaveId.Value.Equals(Guid.Empty))
        {
            throw new InvalidOperationException(
                "Snapshot config 'saveId' is required. List saves with 'list-saves' and paste the exact save id; " +
                "the helper never selects a save automatically.");
        }

        if (string.IsNullOrWhiteSpace(raw.WorkingDirectory))
        {
            throw new InvalidOperationException(
                "Snapshot config 'workingDirectory' is required (for example \"./snapshot-data\").");
        }

        string configDirectory = Path.GetDirectoryName(fullConfigPath) ?? Directory.GetCurrentDirectory();
        string resolvedWorkingDirectory = Path.GetFullPath(
            Path.Combine(configDirectory, raw.WorkingDirectory.Trim()));
        return new SnapshotConfig(baseUrl, raw.SaveId.Value, resolvedWorkingDirectory);
    }

    private static Guid? GetOptionalSaveId(JsonElement root)
    {
        if (!TryGetProperty(root, "saveId", out JsonElement element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Snapshot config 'saveId' must be a GUID string.");
        }

        string? raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Guid.TryParse(raw.Trim(), out Guid parsed) || parsed.Equals(Guid.Empty))
        {
            throw new InvalidOperationException(
                "Snapshot config 'saveId' must be a non-empty GUID identifying the save to snapshot.");
        }

        return parsed;
    }

    private static string? GetOptionalString(JsonElement root, string name)
    {
        if (TryGetProperty(root, name, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
