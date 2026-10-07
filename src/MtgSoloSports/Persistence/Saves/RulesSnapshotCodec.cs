using System.Text.Json;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Version dispatcher for the immutable rules snapshot JSON stored in
/// <c>RulesSnapshots.RulesJson</c>. v1 payloads decode through
/// <see cref="RulesSnapshotDocument"/> into <see cref="RulesV1"/>; v2 tiered
/// payloads decode through <see cref="RulesV2SnapshotDocument"/> into
/// <see cref="RulesV2"/> and v3 tiered-prestige payloads decode through
/// <see cref="RulesV3SnapshotDocument"/> into <see cref="RulesV3"/>
/// (which derives from <see cref="RulesV2"/> which derives from
/// <see cref="RulesV1"/> so existing kernel signatures keep working).
/// Encoding dispatches on the runtime type.
/// Database-schema migration never touches these payloads; game-rule migration
/// is explicit and never implicit.
/// </summary>
public static class RulesSnapshotCodec
{
    public static IReadOnlySet<int> SupportedVersions { get; } = new HashSet<int> { RulesV1.RulesVersion, RulesV2.RulesVersion, RulesV3.RulesVersion };

    public static int PeekVersion(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("version", out JsonElement versionElement) ||
                versionElement.ValueKind != JsonValueKind.Number ||
                !versionElement.TryGetInt32(out int version))
            {
                throw new InvalidOperationException("Rules snapshot payload has no numeric 'version'.");
            }

            return version;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Rules snapshot payload is not valid JSON.", ex);
        }
    }

    public static string Encode(RulesV1 rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules is RulesV3 prestige)
        {
            return RulesV3SnapshotDocument.FromRules(prestige).ToJson();
        }

        if (rules is RulesV2 tiered)
        {
            return RulesV2SnapshotDocument.FromRules(tiered).ToJson();
        }

        return RulesSnapshotDocument.FromRules(rules).ToJson();
    }

    /// <summary>
    /// Decodes any supported snapshot version. v1 payloads return <see cref="RulesV1"/>;
    /// v2 payloads return <see cref="RulesV2"/> and v3 payloads return
    /// <see cref="RulesV3"/> as their <see cref="RulesV1"/> base.
    /// Unknown versions abort; historical results are never reinterpreted.
    /// </summary>
    public static RulesV1 Decode(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        int version = PeekVersion(json);
        return version switch
        {
            1 => RulesSnapshotDocument.FromJson(json).ToRules(),
            2 => RulesV2SnapshotDocument.FromJson(json).ToRules(),
            3 => RulesV3SnapshotDocument.FromJson(json).ToRules(),
            _ => throw new InvalidOperationException(
                $"Rules version {version} is not supported by this build (supports v1, v2 and v3). " +
                "Game-rule migration is separate from database-schema migration and is not performed automatically."),
        };
    }
}
