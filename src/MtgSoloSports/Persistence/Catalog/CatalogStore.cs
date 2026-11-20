using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Persistence.Catalog;

/// <summary>
/// Filesystem + SQLite orchestration for the shared MTG creature catalog.
/// The catalog database is independent of save databases: imports replace the
/// candidate set atomically, while saves later copy their own snapshots.
/// Schema initialization (creation, legacy baselining, migrations) runs
/// through <see cref="CatalogSchemaMigrator"/> once per process: on the
/// import path (the only path that creates the file) and on the first read
/// against an already existing file, so a catalog from an older application
/// version is migrated or guarded before any read can hit a raw schema error.
/// Every later read opens a plain connection with no schema round-trip and
/// reports an empty catalog when the file is absent.
/// This is catalog-scoped file management, not a generic repository.
/// </summary>
public sealed class CatalogStore
{
    private readonly IOptions<CatalogStorageOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly CatalogDbContextFactory _factory;
    private readonly SemaphoreSlim _schemaVerificationGate = new(1, 1);
    private volatile bool _schemaVerified;

    public CatalogStore(
        IOptions<CatalogStorageOptions> options,
        IHostEnvironment environment,
        CatalogDbContextFactory factory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public string GetCatalogFilePath()
    {
        string configured = _options.Value.CatalogPath;
        string relative = string.IsNullOrWhiteSpace(configured) ? "catalog/catalog.db" : configured.Trim();
        return Path.GetFullPath(Path.IsPathRooted(relative) ? relative : Path.Combine(_environment.ContentRootPath, relative));
    }

    /// <summary>
    /// Opens a plain catalog connection (no schema work). Per-connection
    /// pragmas come from <see cref="CatalogSqliteConnectionInterceptor"/>.
    /// The caller owns disposal.
    /// </summary>
    public CatalogDbContext OpenDbContext()
    {
        string path = GetCatalogFilePath();
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return _factory.Create(path);
    }

    /// <summary>
    /// Replaces the catalog candidate set atomically from collapsed athletes.
    /// Athletes must already be collapsed by card name in deterministic order.
    /// This is the only path that creates the catalog file: schema
    /// creation/migration runs here unconditionally, and the successful pass
    /// also satisfies the read-side schema gate.
    /// </summary>
    public async Task<CatalogImportResult> ImportAsync(
        IReadOnlyList<BulkCardRecord> rawRecords,
        IReadOnlyList<CatalogAthlete> athletes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawRecords);
        ArgumentNullException.ThrowIfNull(athletes);

        (int total, int eligible, int unique, int skippedTokens, int skippedNonCreature, int skippedAmbiguous) = BulkCatalogParser.CountCollapse(rawRecords);

        await CatalogSchemaMigrator.EnsureSchemaAsync(_factory, GetCatalogFilePath(), cancellationToken).ConfigureAwait(false);
        _schemaVerified = true;

        using CatalogDbContext context = OpenDbContext();
        using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await context.Athletes.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        List<CatalogAthleteEntity> entities = new(athletes.Count);
        foreach (CatalogAthlete athlete in athletes)
        {
            entities.Add(CatalogAthleteEntity.FromAthlete(athlete));
        }

        if (entities.Count > 0)
        {
            await context.Athletes.AddRangeAsync(entities, cancellationToken).ConfigureAwait(false);
        }

        _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<SportingColor, int> counts = CountByColor(athletes);
        return new CatalogImportResult(total, eligible, unique, counts, skippedTokens, skippedNonCreature, skippedAmbiguous);
    }

    /// <summary>
    /// Once-per-process schema gate for reads: the first access to an
    /// existing catalog file adopts it into the migrations story (legacy
    /// baselining or pending migrations) or fails loudly with re-import
    /// guidance; every later read is a plain connection with no schema work.
    /// A missing file stays an empty catalog — reads never create it — and a
    /// failed verification is never cached, so reads stay loud until the
    /// file is deleted and re-imported.
    /// </summary>
    private async Task EnsureSchemaVerifiedAsync(CancellationToken cancellationToken)
    {
        if (_schemaVerified || !File.Exists(GetCatalogFilePath()))
        {
            return;
        }

        await _schemaVerificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaVerified)
            {
                return;
            }

            await CatalogSchemaMigrator.EnsureSchemaAsync(_factory, GetCatalogFilePath(), cancellationToken).ConfigureAwait(false);
            _schemaVerified = true;
        }
        finally
        {
            _schemaVerificationGate.Release();
        }
    }

    /// <summary>
    /// Reports persisted unique-athlete counts per sporting color. All eight
    /// colors are always present (zero when absent). A missing catalog file is
    /// an empty catalog: no file is created and no schema round-trip runs.
    /// </summary>
    public async Task<IReadOnlyDictionary<SportingColor, int>> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaVerifiedAsync(cancellationToken).ConfigureAwait(false);
        if (!File.Exists(GetCatalogFilePath()))
        {
            return EmptyCounts();
        }

        using CatalogDbContext context = OpenDbContext();

        Dictionary<SportingColor, int> counts = EmptyCounts();
        List<ColorCount> rows = await context.Athletes
            .AsNoTracking()
            .GroupBy(e => e.SportingColor)
            .Select(g => new ColorCount(g.Key, g.Count()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (ColorCount row in rows)
        {
            counts[(SportingColor)row.SportingColor] = row.Count;
        }

        return counts;
    }

    /// <summary>
    /// Total persisted unique athletes across all sporting colors. A missing
    /// catalog file is an empty catalog (zero), created by no read.
    /// </summary>
    public async Task<int> GetTotalAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaVerifiedAsync(cancellationToken).ConfigureAwait(false);
        if (!File.Exists(GetCatalogFilePath()))
        {
            return 0;
        }

        using CatalogDbContext context = OpenDbContext();
        return await context.Athletes.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists every persisted catalog candidate for save-universe selection.
    /// A missing catalog file is an empty catalog (no athletes), created by
    /// no read. The selector re-sorts by name, so database row order is never
    /// trusted as sporting input.
    /// </summary>
    public async Task<IReadOnlyList<CatalogAthlete>> ListAthletesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaVerifiedAsync(cancellationToken).ConfigureAwait(false);
        if (!File.Exists(GetCatalogFilePath()))
        {
            return [];
        }

        using CatalogDbContext context = OpenDbContext();
        List<CatalogAthleteEntity> entities = await context.Athletes
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<CatalogAthlete> athletes = new(entities.Count);
        foreach (CatalogAthleteEntity entity in entities)
        {
            athletes.Add(entity.ToAthlete());
        }

        return athletes;
    }

    /// <summary>
    /// Save-creation gate: throws when any sporting color has fewer than
    /// 256 unique athletes. MSS-006 calls this before universe selection.
    /// </summary>
    public async Task EnsureSufficientForSaveAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<SportingColor, int> counts = await GetCountsAsync(cancellationToken).ConfigureAwait(false);
        CatalogQuotas.EnsureSufficient(counts);
    }

    internal static Dictionary<SportingColor, int> CountByColor(IReadOnlyList<CatalogAthlete> athletes)
    {
        Dictionary<SportingColor, int> counts = EmptyCounts();
        foreach (CatalogAthlete athlete in athletes)
        {
            counts[athlete.SportingColor]++;
        }

        return counts;
    }

    internal static Dictionary<SportingColor, int> EmptyCounts()
    {
        Dictionary<SportingColor, int> counts = new();
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            counts[color] = 0;
        }

        return counts;
    }

    private sealed record ColorCount(int SportingColor, int Count);
}
