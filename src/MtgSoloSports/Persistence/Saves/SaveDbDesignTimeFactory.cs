using Microsoft.EntityFrameworkCore;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations</c> can build the model
/// without a real save file. The connection string is never opened during scaffolding.
/// </summary>
public sealed class SaveDbDesignTimeFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<SaveDbContext>
{
    public SaveDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<SaveDbContext> options = new DbContextOptionsBuilder<SaveDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new SaveDbContext(options);
    }
}
