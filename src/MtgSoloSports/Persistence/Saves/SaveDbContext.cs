using Microsoft.EntityFrameworkCore;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One <see cref="SaveDbContext"/> maps exactly one save SQLite file.
/// Feature slices may use this context directly; generic repositories and
/// UnitOfWork abstractions are forbidden.
/// </summary>
public sealed class SaveDbContext : DbContext
{
    public SaveDbContext(DbContextOptions<SaveDbContext> options)
        : base(options)
    {
    }

    public DbSet<SaveMetadataEntity> SaveMetadata => Set<SaveMetadataEntity>();

    public DbSet<RulesSnapshotEntity> RulesSnapshots => Set<RulesSnapshotEntity>();

    public DbSet<RngStateEntity> RngStates => Set<RngStateEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<SaveMetadataEntity>(entity =>
        {
            entity.ToTable("SaveMetadata", table => table.HasCheckConstraint("CK_SaveMetadata_SingleRow", "\"Id\" = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.SaveId).IsRequired();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CreatedUtc).IsRequired();
            entity.Property(e => e.SchemaVersion).IsRequired();
            entity.Property(e => e.CurrentSeason).IsRequired();
            entity.Property(e => e.Phase).IsRequired().HasMaxLength(64);
        });

        modelBuilder.Entity<RulesSnapshotEntity>(entity =>
        {
            entity.ToTable("RulesSnapshots", table => table.HasCheckConstraint("CK_RulesSnapshots_SingleRow", "\"Id\" = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.RulesVersion).IsRequired();
            entity.Property(e => e.RulesJson).IsRequired();
        });

        modelBuilder.Entity<RngStateEntity>(entity =>
        {
            entity.ToTable("RngStates", table => table.HasCheckConstraint("CK_RngStates_SingleRow", "\"Id\" = 1"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Algorithm).IsRequired().HasMaxLength(64);
            entity.Property(e => e.AlgorithmVersion).IsRequired();
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.Stream).IsRequired();
        });
    }

    /// <summary>
    /// Stages an RNG state update on the tracked single-row entity.
    /// Callers commit it with <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// in the same transaction as generated simulation results.
    /// </summary>
    public void ApplyRngState(Pcg32State next)
    {
        RngStateEntity entity = RngStates.Local.SingleOrDefault(e => e.Id == 1)
            ?? throw new InvalidOperationException("RNG state row is not tracked. Load it before applying a new state.");
        entity.Algorithm = Pcg32V1.AlgorithmName;
        entity.AlgorithmVersion = Pcg32V1.AlgorithmVersion;
        entity.State = unchecked((long)next.State);
        entity.Stream = unchecked((long)next.Stream);
    }
}
