using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Saves;

/// <summary>
/// MSS-149 migration policy guards, kept as ordinary suite tests so CI
/// fails loudly on policy violations: every save migration must carry a
/// <c>.Designer.cs</c> companion whose target model is the model as of
/// that migration (so <c>dotnet ef migrations remove</c>, idempotent
/// scripts and future scaffolding stay truthful), and the committed
/// <c>SaveDbContextModelSnapshot</c> must match the real model at HEAD,
/// so the next scaffolded migration contains exactly the intended change
/// instead of silently absorbing snapshot drift.
/// </summary>
public sealed class SaveMigrationPolicyTests
{
    [Fact]
    public void EveryDefinedMigration_HasADesignerCompanionWithItsTargetModel()
    {
        List<Type> migrations =
        [
            .. typeof(SaveDbContext).Assembly
                .GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract
                    && typeof(Migration).IsAssignableFrom(type)
                    && type.GetCustomAttribute<MigrationAttribute>() is not null)
                .OrderBy(type => type.GetCustomAttribute<MigrationAttribute>()!.Id, StringComparer.Ordinal),
        ];

        migrations.Count.ShouldBeGreaterThan(0);

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (Type migration in migrations)
        {
            MigrationAttribute attribute = migration.GetCustomAttribute<MigrationAttribute>()!;
            ids.Add(attribute.Id).ShouldBeTrue($"duplicate save migration id '{attribute.Id}'");

            MethodInfo? buildTargetModel = migration.GetMethod(
                "BuildTargetModel",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(ModelBuilder)],
                modifiers: null);
            buildTargetModel.ShouldNotBeNull(
                $"save migration '{attribute.Id}' has no .Designer.cs companion; regenerate its Designer at the migration's introducing commit as described in src/MtgSoloSports/Persistence/Saves/Migrations/README.md.");
            buildTargetModel!.DeclaringType.ShouldBe(migration,
                $"save migration '{attribute.Id}' must override BuildTargetModel in its own Designer partial class.");
        }
    }

    [Fact]
    public void ModelSnapshot_MatchesTheRealModelSoTheNextScaffoldedMigrationIsExact()
    {
        DbContextOptions<SaveDbContext> options = new DbContextOptionsBuilder<SaveDbContext>()
            .UseSqlite("Data Source=never.db")
            .Options;
        using SaveDbContext context = new(options);

        IMigrationsAssembly migrationsAssembly = context.GetService<IMigrationsAssembly>();
        migrationsAssembly.ModelSnapshot.ShouldNotBeNull(
            "the save migrations snapshot class is missing; regenerate it as described in src/MtgSoloSports/Persistence/Saves/Migrations/README.md.");

        // Mirror the dotnet ef scaffolder pipeline: initialize the snapshot
        // model with runtime dependencies, then ask the migrations differ
        // for changes against the current design-time model. An empty diff
        // means the next scaffolded migration contains exactly the
        // contributor's model change and no snapshot drift.
        IModel snapshotModel = migrationsAssembly.ModelSnapshot!.Model;
        IModelRuntimeInitializer initializer = context.GetService<IModelRuntimeInitializer>();
        IModel processed = initializer.Initialize(snapshotModel, designTime: true);

        IMigrationsModelDiffer differ = context.GetService<IMigrationsModelDiffer>();
        IModel current = context.GetService<IDesignTimeModel>().Model;
        differ.HasDifferences(processed.GetRelationalModel(), current.GetRelationalModel()).ShouldBeFalse(
            "the committed SaveDbContextModelSnapshot drifted from the real model at HEAD; regenerate the snapshot with the EF Core toolchain (see src/MtgSoloSports/Persistence/Saves/Migrations/README.md).");
    }
}
