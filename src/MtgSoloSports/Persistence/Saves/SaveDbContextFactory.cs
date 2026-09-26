using Microsoft.EntityFrameworkCore;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Builds a <see cref="SaveDbContext"/> bound to one save file.
/// The interceptor keeps per-connection SQLite behavior consistent (WAL file
/// itself is enabled per database at creation time).
/// </summary>
public sealed class SaveDbContextFactory
{
    private readonly SaveSqliteConnectionInterceptor _interceptor;

    public SaveDbContextFactory(SaveSqliteConnectionInterceptor interceptor)
    {
        _interceptor = interceptor ?? throw new ArgumentNullException(nameof(interceptor));
    }

    public SaveDbContext Create(string saveFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveFilePath);
        DbContextOptions<SaveDbContext> options = new DbContextOptionsBuilder<SaveDbContext>()
            .UseSqlite($"Data Source={saveFilePath}")
            .AddInterceptors(_interceptor)
            .Options;
        return new SaveDbContext(options);
    }
}
