using Microsoft.EntityFrameworkCore;

namespace MtgSoloSports.Persistence.Catalog;

/// <summary>
/// Builds a <see cref="CatalogDbContext"/> bound to the shared catalog file.
/// </summary>
public sealed class CatalogDbContextFactory
{
    public CatalogDbContext Create(string catalogFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogFilePath);
        DbContextOptions<CatalogDbContext> options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite($"Data Source={catalogFilePath}")
            .Options;
        return new CatalogDbContext(options);
    }
}
