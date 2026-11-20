using Microsoft.EntityFrameworkCore;

namespace MtgSoloSports.Persistence.Catalog;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the catalog
/// model without a real catalog file. The connection string is never opened
/// during scaffolding.
/// </summary>
public sealed class CatalogDbDesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<CatalogDbContext> options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new CatalogDbContext(options);
    }
}
