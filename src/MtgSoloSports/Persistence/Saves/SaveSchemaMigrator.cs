using Microsoft.EntityFrameworkCore;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Database-schema migration runner for one save file. Applies pending EF
/// Core migrations (tables, indexes, constraints) and records the resulting
/// <see cref="SaveSchemaVersion.Current"/> as schema bookkeeping in the
/// single-row metadata table. This type is schema-only by construction: it
/// never reads, writes, or migrates <c>RulesSnapshots</c> sporting rows or
/// <c>RngStates</c> rows. Game-rule migration is owned exclusively by
/// <see cref="SaveRulesCompatibility"/> and is never performed here.
/// Callers create and verify a recoverable checkpoint before invoking this
/// runner when pending migrations exist.
/// </summary>
public static class SaveSchemaMigrator
{
    /// <summary>
    /// Lists EF Core migrations pending for the save file.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetPendingMigrationsAsync(
        SaveDbContextFactory factory,
        string saveFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveFilePath);
        if (!File.Exists(saveFilePath))
        {
            throw new FileNotFoundException("Save database file was not found.", saveFilePath);
        }

        using SaveDbContext context = factory.Create(saveFilePath);
        IEnumerable<string> pending = await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
        return pending.ToList();
    }

    /// <summary>
    /// Applies pending schema migrations, stamps the metadata schema version,
    /// and verifies the file still opens with its required rows. Contains no
    /// game-rule logic; sporting-math safety is verified by the caller through
    /// <see cref="SaveRulesCompatibility"/> before/after comparison.
    /// </summary>
    public static async Task ApplyPendingAsync(
        SaveDbContextFactory factory,
        string saveFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveFilePath);
        if (!File.Exists(saveFilePath))
        {
            throw new FileNotFoundException("Save database file was not found.", saveFilePath);
        }

        using (SaveDbContext context = factory.Create(saveFilePath))
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        using (SaveDbContext context = factory.Create(saveFilePath))
        {
            SaveMetadataEntity metadata = await context.SaveMetadata
                .SingleAsync(e => e.Id == 1, cancellationToken)
                .ConfigureAwait(false);
            metadata.SchemaVersion = SaveSchemaVersion.Current;
            _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        using (SaveDbContext context = factory.Create(saveFilePath))
        {
            bool hasMetadata = await context.SaveMetadata
                .AsNoTracking()
                .AnyAsync(e => e.Id == 1, cancellationToken)
                .ConfigureAwait(false);
            bool hasRules = await context.RulesSnapshots
                .AsNoTracking()
                .AnyAsync(e => e.Id == 1, cancellationToken)
                .ConfigureAwait(false);
            bool hasRng = await context.RngStates
                .AsNoTracking()
                .AnyAsync(e => e.Id == 1, cancellationToken)
                .ConfigureAwait(false);
            if (!hasMetadata || !hasRules || !hasRng)
            {
                throw new InvalidOperationException(
                    "Schema migration completed but the save is missing required metadata, rules, or RNG rows.");
            }
        }
    }
}
