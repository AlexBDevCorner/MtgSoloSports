using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Athletes;

public sealed class AthleteProfileIndexTests
{
    [Fact]
    public void Model_HasAthleteLeadingIndexes_WithoutRedundantSingle()
    {
        IModel model = BuildModel();
        IEntityType? movements = model.FindEntityType(typeof(MovementEntity));
        movements.ShouldNotBeNull();

        movements!.GetIndexes().Any(i =>
                !i.IsUnique &&
                i.Properties.Select(p => p.Name).SequenceEqual(
                    ["SaveAthleteId", "ToSeasonId", "Kind"], StringComparer.Ordinal))
            .ShouldBeTrue();

        movements!.GetIndexes().Any(i =>
                i.IsUnique &&
                i.Properties.Select(p => p.Name).SequenceEqual(
                    ["ToSeasonId", "SaveAthleteId"], StringComparer.Ordinal))
            .ShouldBeTrue();

        IEntityType? stories = model.FindEntityType(typeof(StoryEventEntity));
        stories.ShouldNotBeNull();

        stories!.GetIndexes().Any(i =>
                !i.IsUnique &&
                i.Properties.Select(p => p.Name).SequenceEqual(
                    ["SaveAthleteId", "Id"], StringComparer.Ordinal))
            .ShouldBeTrue();

        stories!.GetIndexes().Any(i =>
                !i.IsUnique &&
                i.Properties.Select(p => p.Name).SequenceEqual(
                    ["SaveAthleteId"], StringComparer.Ordinal))
            .ShouldBeFalse();

        stories!.GetIndexes().Any(i =>
                i.IsUnique &&
                i.Properties.Select(p => p.Name).SequenceEqual(
                    ["SaveAthleteId", "EventType", "DedupKey"], StringComparer.Ordinal))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Sqlite_QueryPlans_UseAthleteIndexes()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Index Plans", 101UL, 202UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            using SaveDbContext context = store.OpenDbContext(saveId);

            string movementsPlan = await ExplainAsync(
                context,
                "SELECT \"Id\" FROM \"Movements\" WHERE \"SaveAthleteId\" = 1 ORDER BY \"ToSeasonId\", \"Kind\"");
            movementsPlan.ShouldContain("IX_Movements_SaveAthleteId_ToSeasonId_Kind");

            string storiesPlan = await ExplainAsync(
                context,
                "SELECT \"Id\" FROM \"StoryEvents\" WHERE \"SaveAthleteId\" = 1 ORDER BY \"Id\" DESC LIMIT 20");
            storiesPlan.ShouldContain("IX_StoryEvents_SaveAthleteId_Id");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MigrationCache_SkipsRepeatedPendingChecks_AndInvalidatesOnReplace()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Migration Cache", 303UL, 404UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            string path = store.GetSaveFilePath(saveId);

            store.IsMigrationVerified(path, saveId).ShouldBeFalse();
            await store.EnsureMigratedAsync(saveId);
            store.IsMigrationVerified(path, saveId).ShouldBeTrue();

            await store.EnsureMigratedAsync(saveId);
            store.IsMigrationVerified(path, saveId).ShouldBeTrue();

            store.InvalidateMigrationVerified(saveId);
            store.IsMigrationVerified(path, saveId).ShouldBeFalse();
            await store.EnsureMigratedAsync(saveId);
            store.IsMigrationVerified(path, saveId).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IModel BuildModel()
    {
        DbContextOptions<SaveDbContext> options = new DbContextOptionsBuilder<SaveDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using SaveDbContext context = new(options);
        return context.Model;
    }

    private static async Task<string> ExplainAsync(SaveDbContext context, string sql)
    {
        using SqliteCommand command = new(
            $"EXPLAIN QUERY PLAN {sql}",
            (SqliteConnection)context.Database.GetDbConnection());
        if (command.Connection?.State != System.Data.ConnectionState.Open)
        {
            await command.Connection!.OpenAsync().ConfigureAwait(false);
        }

        using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        List<string> details = [];
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            details.Add(reader.GetString(3));
        }

        return string.Join(" | ", details);
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-profile-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
