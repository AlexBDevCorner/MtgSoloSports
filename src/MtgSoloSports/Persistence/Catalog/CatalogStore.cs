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
/// This is catalog-scoped file management, not a generic repository.
/// </summary>
public sealed class CatalogStore
{
    private readonly IOptions<CatalogStorageOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly CatalogDbContextFactory _factory;

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
    /// </summary>
    public async Task<CatalogImportResult> ImportAsync(
        IReadOnlyList<BulkCardRecord> rawRecords,
        IReadOnlyList<CatalogAthlete> athletes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawRecords);
        ArgumentNullException.ThrowIfNull(athletes);

        (int total, int eligible, int unique, int skippedTokens, int skippedNonCreature) = BulkCatalogParser.CountCollapse(rawRecords);

        using CatalogDbContext context = OpenDbContext();
        _ = await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);

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
        return new CatalogImportResult(total, eligible, unique, counts, skippedTokens, skippedNonCreature);
    }

    /// <summary>
    /// Reports persisted unique-athlete counts per sporting color. All eight
    /// colors are always present (zero when absent).
    /// </summary>
    public async Task<IReadOnlyDictionary<SportingColor, int>> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        using CatalogDbContext context = OpenDbContext();
        _ = await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

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
    /// Total persisted unique athletes across all sporting colors.
    /// </summary>
    public async Task<int> GetTotalAsync(CancellationToken cancellationToken = default)
    {
        using CatalogDbContext context = OpenDbContext();
        _ = await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        return await context.Athletes.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
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
