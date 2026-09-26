using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Catalog.ImportCatalog;

namespace MtgSoloSports.Persistence.Catalog;

/// <summary>
/// Global MTG catalog database, separate from per-save databases.
/// Holds imported athlete candidates; each save later copies its selected
/// 2,048-athlete snapshot so catalog changes cannot rewrite history.
/// </summary>
public sealed class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<CatalogAthleteEntity> Athletes => Set<CatalogAthleteEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<CatalogAthleteEntity>(entity =>
        {
            entity.ToTable("CatalogAthletes");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(256);
            entity.Property(e => e.SportingColor).IsRequired();
            entity.Property(e => e.CreatureTypesJson).IsRequired();
            entity.Property(e => e.FrontColors).IsRequired().HasMaxLength(8);
            entity.Property(e => e.ManaCost).IsRequired();
            entity.Property(e => e.TypeLine).IsRequired();
            entity.Property(e => e.ImageUrl).HasMaxLength(1024);
            entity.Property(e => e.SetCode).HasMaxLength(16);
        });
    }

    /// <summary>
    /// Maps collapsed athletes to entities in deterministic name order.
    /// </summary>
    public static CatalogAthleteEntity ToEntity(CatalogAthlete athlete)
    {
        ArgumentNullException.ThrowIfNull(athlete);
        return CatalogAthleteEntity.FromAthlete(athlete);
    }
}
