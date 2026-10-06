using System.Text.Json;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Version metadata carried inside every portable save artifact alongside the
/// SQLite save database. The manifest lets import validate schema/rules
/// compatibility before touching any live save file.
/// </summary>
public sealed record SaveBundleManifest(
    string Format,
    int FormatVersion,
    Guid SaveId,
    string Name,
    DateTimeOffset CreatedUtc,
    int SchemaVersion,
    int RulesVersion,
    string RngAlgorithm,
    int RngVersion,
    int CurrentSeason,
    string Phase,
    string DatabaseFileName,
    string DatabaseSha256,
    DateTimeOffset ExportedUtc,
    string Exporter)
{
    public const string ExpectedFormat = "mtgsolosports-save";

    public const int CurrentFormatVersion = 1;

    public const string ManifestEntryName = "manifest.json";

    public const string DatabaseEntryName = "save.db";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static SaveBundleManifest FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        SaveBundleManifest? manifest = JsonSerializer.Deserialize<SaveBundleManifest>(json, JsonOptions);
        return manifest ?? throw new InvalidOperationException("Save bundle manifest payload is empty.");
    }

    /// <summary>
    /// Structural self-check for a freshly built manifest. Throws on any violation.
    /// </summary>
    public void ValidateForExport()
    {
        if (!string.Equals(Format, ExpectedFormat, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Save bundle format must be '{ExpectedFormat}', was '{Format}'.");
        }

        if (FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Save bundle format version must be {CurrentFormatVersion}, was {FormatVersion}.");
        }

        if (SaveId == Guid.Empty)
        {
            throw new InvalidOperationException("Save bundle manifest has an empty save id.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("Save bundle manifest has an empty save name.");
        }

        if (SchemaVersion != SaveSchemaVersion.Current)
        {
            throw new InvalidOperationException(
                $"Save bundle schema version must be {SaveSchemaVersion.Current}, was {SchemaVersion}.");
        }

        if (!RulesSnapshotCodec.SupportedVersions.Contains(RulesVersion))
        {
            throw new InvalidOperationException(
                $"Save bundle rules version must be v1 or v2, was {RulesVersion}.");
        }

        if (!string.Equals(RngAlgorithm, Pcg32V1.AlgorithmName, StringComparison.Ordinal)
            || RngVersion != Pcg32V1.AlgorithmVersion)
        {
            throw new InvalidOperationException(
                $"Save bundle RNG must be {Pcg32V1.AlgorithmName} v{Pcg32V1.AlgorithmVersion}, was '{RngAlgorithm}' v{RngVersion}.");
        }

        if (CurrentSeason < 1)
        {
            throw new InvalidOperationException(
                $"Save bundle current season must be at least 1, was {CurrentSeason}.");
        }

        if (string.IsNullOrWhiteSpace(Phase))
        {
            throw new InvalidOperationException("Save bundle manifest has an empty phase.");
        }

        if (!string.Equals(DatabaseFileName, DatabaseEntryName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Save bundle database entry must be '{DatabaseEntryName}', was '{DatabaseFileName}'.");
        }

        EnsureChecksumShape(DatabaseSha256);
    }

    /// <summary>
    /// Structural validation for an incoming artifact. Verifies shape and
    /// nonce fields only; sporting compatibility (schema/rules/RNG support) is
    /// enforced separately by <see cref="SaveRulesCompatibility"/> so database
    /// schema migration stays distinct from game-rule migration.
    /// </summary>
    public void ValidateForImport()
    {
        if (!string.Equals(Format, ExpectedFormat, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unsupported save bundle format '{Format}'. Expected '{ExpectedFormat}'.");
        }

        if (FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported save bundle format version {FormatVersion}. This build reads version {CurrentFormatVersion}.");
        }

        if (SaveId == Guid.Empty)
        {
            throw new InvalidOperationException("Save bundle manifest has an empty save id.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("Save bundle manifest has an empty save name.");
        }

        if (SchemaVersion < 1)
        {
            throw new InvalidOperationException(
                $"Save bundle schema version {SchemaVersion} is invalid.");
        }

        if (SchemaVersion > SaveSchemaVersion.Current)
        {
            throw new InvalidOperationException(
                $"Save bundle schema version {SchemaVersion} is newer than supported version {SaveSchemaVersion.Current}. " +
                "Import is rejected; a newer application build is required.");
        }

        if (CurrentSeason < 1)
        {
            throw new InvalidOperationException(
                $"Save bundle current season must be at least 1, was {CurrentSeason}.");
        }

        if (string.IsNullOrWhiteSpace(Phase))
        {
            throw new InvalidOperationException("Save bundle manifest has an empty phase.");
        }

        if (!string.Equals(DatabaseFileName, DatabaseEntryName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Save bundle database entry must be '{DatabaseEntryName}', was '{DatabaseFileName}'.");
        }

        EnsureChecksumShape(DatabaseSha256);
    }

    internal static void EnsureChecksumShape(string checksum)
    {
        if (string.IsNullOrWhiteSpace(checksum) || checksum.Length != 64)
        {
            throw new InvalidOperationException("Save bundle database checksum must be 64 hexadecimal characters.");
        }

        foreach (char c in checksum)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex)
            {
                throw new InvalidOperationException("Save bundle database checksum must be 64 hexadecimal characters.");
            }
        }
    }
}
