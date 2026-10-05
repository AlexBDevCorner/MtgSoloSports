using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Game-rule compatibility gate for save import and migration verification.
/// Database-schema migration (EF Core tables) and game-rule migration
/// (sporting mathematics in the immutable rules snapshot) are separate
/// concepts: this gate validates sporting compatibility and never mutates
/// schema, data, or the persisted rules snapshot. Any mismatch aborts the
/// operation instead of silently upgrading sporting rules.
/// </summary>
public static class SaveRulesCompatibility
{
    /// <summary>
    /// Validates that an imported artifact's sporting rules are supported by
    /// this build. Rules v1 (single feeder tier) and v2 (tiered Superleague /
    /// Feeder 1-3) are both importable; anything else is rejected.
    /// Game-rule migration stays explicit and is never an implicit side effect
    /// of import or schema migration.
    /// </summary>
    public static void EnsureImportableRules(SaveBundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!RulesSnapshotCodec.SupportedVersions.Contains(manifest.RulesVersion))
        {
            throw new InvalidOperationException(
                $"Save bundle rules version {manifest.RulesVersion} is not supported by this build (supports v1 and v2). " +
                "Game-rule migration is separate from database-schema migration and is not performed automatically on import.");
        }

        if (!string.Equals(manifest.RngAlgorithm, Pcg32V1.AlgorithmName, StringComparison.Ordinal)
            || manifest.RngVersion != Pcg32V1.AlgorithmVersion)
        {
            throw new InvalidOperationException(
                $"Save bundle RNG '{manifest.RngAlgorithm}' v{manifest.RngVersion} is not supported. " +
                $"This build requires {Pcg32V1.AlgorithmName} v{Pcg32V1.AlgorithmVersion}.");
        }
    }

    /// <summary>
    /// Rebuilds and validates the persisted rules snapshot. v1 snapshots use
    /// the v1 compatibility path; v2 tiered snapshots decode to
    /// <see cref="RulesV2"/>. Throws with a rules-specific message when
    /// sporting mathematics cannot be trusted.
    /// </summary>
    public static RulesV1 ReadSnapshotRules(string rulesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesJson);
        try
        {
            RulesV1 rules = RulesSnapshotCodec.Decode(rulesJson);
            rules.Validate();
            return rules;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Save rules snapshot is invalid or incompatible: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Proves a database-schema migration left sporting mathematics untouched
    /// by comparing the immutable rules snapshot JSON before and after.
    /// Any difference aborts the caller; schema migration must never change rules.
    /// </summary>
    public static void EnsureSnapshotUnchanged(string beforeJson, string afterJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(beforeJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(afterJson);
        if (!string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Database-schema migration changed the game-rules snapshot. " +
                "Schema migration and game-rule migration are separate; the migration was rejected.");
        }
    }
}
