using Microsoft.EntityFrameworkCore;

namespace MtgSoloSports.Persistence.Catalog;

/// <summary>
/// Builds a <see cref="CatalogDbContext"/> bound to the shared catalog file.
/// The interceptor keeps per-connection SQLite behavior consistent with saves
/// (foreign keys, busy timeout); the WAL mode itself is enabled per file once
/// by <see cref="CatalogSchemaMigrator"/> when the schema is created.
/// </summary>
public sealed class CatalogDbContextFactory
{
    private readonly CatalogSqliteConnectionInterceptor _interceptor;

    public CatalogDbContextFactory(CatalogSqliteConnectionInterceptor interceptor)
    {
        _interceptor = interceptor ?? throw new ArgumentNullException(nameof(interceptor));
    }

    public CatalogDbContext Create(string catalogFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogFilePath);
        DbContextOptions<CatalogDbContext> options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite($"Data Source={catalogFilePath}")
            .AddInterceptors(_interceptor)
            .Options;
        return new CatalogDbContext(options);
    }
}
