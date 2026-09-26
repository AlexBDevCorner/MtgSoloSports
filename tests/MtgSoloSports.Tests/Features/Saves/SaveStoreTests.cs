using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Saves;

public sealed class SaveStoreTests
{
    [Fact]
    public async Task CreateAsync_PersistsMetadataRulesAndRng()
    {
        var (store, root) = CreateStore();
        try
        {
            DateTimeOffset before = DateTimeOffset.UtcNow;
            SaveStore.SaveDetailRecord created = await store.CreateAsync("First Save", 123UL, 456UL);
            DateTimeOffset after = DateTimeOffset.UtcNow;

            created.SaveId.ShouldNotBe(Guid.Empty);
            created.Name.ShouldBe("First Save");
            created.SchemaVersion.ShouldBe(SaveSchemaVersion.Current);
            created.CurrentSeason.ShouldBe(1);
            created.Phase.ShouldBe("SeasonInProgress");
            created.RngAlgorithm.ShouldBe(Pcg32V1.AlgorithmName);
            created.RngVersion.ShouldBe(Pcg32V1.AlgorithmVersion);
            created.RulesVersion.ShouldBe(RulesV1.RulesVersion);
            created.CreatedUtc.ShouldBeInRange(before.AddSeconds(-1), after.AddSeconds(1));

            Pcg32State expected = new Pcg32V1(123UL, 456UL).Snapshot();
            created.RngState.ShouldBe(expected.State);
            created.RngStream.ShouldBe(expected.Stream);

            File.Exists(store.GetSaveFilePath(created.SaveId)).ShouldBeTrue();

            string expectedRulesJson = RulesSnapshotDocument.FromRules(RulesV1.CreateDefault()).ToJson();
            created.RulesJson.ShouldBe(expectedRulesJson);
            RulesV1 rules = RulesSnapshotDocument.FromJson(created.RulesJson).ToRules();
            rules.LeagueSize.ShouldBe(32);
            rules.RoundsPerStage.ShouldBe(16);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateThenListThenOpen_RoundTrips()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.SaveDetailRecord first = await store.CreateAsync("Alpha", 1UL, 2UL);
            SaveStore.SaveDetailRecord second = await store.CreateAsync("Beta", 3UL, 4UL);

            IReadOnlyList<SaveStore.SaveRecord> listed = await store.ListAsync();
            listed.Count.ShouldBe(2);
            listed.Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(["Alpha", "Beta"]);

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(first.SaveId);
            reopened.SaveId.ShouldBe(first.SaveId);
            reopened.Name.ShouldBe("Alpha");
            reopened.RngState.ShouldBe(new Pcg32V1(1UL, 2UL).Snapshot().State);

            SaveStore.SaveDetailRecord reopenedSecond = await store.OpenAsync(second.SaveId);
            reopenedSecond.Name.ShouldBe("Beta");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SavesAreIsolatedPerFile_WithWalEnabled()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.SaveDetailRecord first = await store.CreateAsync("One", 11UL, 12UL);
            SaveStore.SaveDetailRecord second = await store.CreateAsync("Two", 13UL, 14UL);

            string firstPath = store.GetSaveFilePath(first.SaveId);
            string secondPath = store.GetSaveFilePath(second.SaveId);
            firstPath.Equals(secondPath, StringComparison.Ordinal).ShouldBeFalse();
            File.Exists(firstPath).ShouldBeTrue();
            File.Exists(secondPath).ShouldBeTrue();
            Directory.GetFiles(root, "*.db").Length.ShouldBe(2);

            string journalMode = await ReadJournalModeAsync(store, first.SaveId);
            journalMode.ShouldBe("wal", StringCompareShould.IgnoreCase);

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(first.SaveId);
            reopened.Name.ShouldBe("One");
            reopened.SaveId.ShouldNotBe(second.SaveId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RngState_CommitsAtomicallyWithSimulationMutation()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.SaveDetailRecord created = await store.CreateAsync("Atomic", 7UL, 9UL);
            Pcg32State expected = AdvanceOnce(7UL, 9UL);

            using (SaveDbContext context = store.OpenDbContext(created.SaveId))
            {
                using var transaction = await context.Database.BeginTransactionAsync();
                SaveMetadataEntity metadata = await context.SaveMetadata.SingleAsync(e => e.Id == 1);
                RngStateEntity rng = await context.RngStates.SingleAsync(e => e.Id == 1);
                metadata.CurrentSeason = 2;
                rng.State = unchecked((long)expected.State);
                rng.Stream = unchecked((long)expected.Stream);
                _ = await context.SaveChangesAsync();
                await transaction.CommitAsync();
            }

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.SaveId);
            reopened.CurrentSeason.ShouldBe(2);
            reopened.RngState.ShouldBe(expected.State);
            reopened.RngStream.ShouldBe(expected.Stream);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RolledBackMutation_PreservesRngAndSeason()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.SaveDetailRecord created = await store.CreateAsync("Rollback", 21UL, 22UL);
            Pcg32State original = new Pcg32V1(21UL, 22UL).Snapshot();
            Pcg32State discarded = AdvanceOnce(21UL, 22UL);
            discarded.State.ShouldNotBe(original.State);

            using (SaveDbContext context = store.OpenDbContext(created.SaveId))
            {
                using var transaction = await context.Database.BeginTransactionAsync();
                SaveMetadataEntity metadata = await context.SaveMetadata.SingleAsync(e => e.Id == 1);
                RngStateEntity rng = await context.RngStates.SingleAsync(e => e.Id == 1);
                metadata.CurrentSeason = 99;
                rng.State = unchecked((long)discarded.State);
                _ = await context.SaveChangesAsync();
                await transaction.RollbackAsync();
            }

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.SaveId);
            reopened.CurrentSeason.ShouldBe(1);
            reopened.RngState.ShouldBe(original.State);
            reopened.RngStream.ShouldBe(original.Stream);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RngStateEntity_RoundTripsEdgeValues()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.SaveDetailRecord created = await store.CreateAsync("Edges", 5UL, 6UL);
            Pcg32State edge = new(ulong.MaxValue, 0UL);

            using (SaveDbContext context = store.OpenDbContext(created.SaveId))
            {
                RngStateEntity rng = await context.RngStates.SingleAsync(e => e.Id == 1);
                rng.State = unchecked((long)edge.State);
                rng.Stream = unchecked((long)edge.Stream);
                _ = await context.SaveChangesAsync();
            }

            SaveStore.SaveDetailRecord reopened = await store.OpenAsync(created.SaveId);
            reopened.RngState.ShouldBe(ulong.MaxValue);
            reopened.RngStream.ShouldBe(0UL);

            RngStateEntity direct = RngStateEntity.FromState(new Pcg32State(0UL, ulong.MaxValue));
            direct.ToState().ShouldBe(new Pcg32State(0UL, ulong.MaxValue));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyOwnFile()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.SaveDetailRecord created = await store.CreateAsync("Doomed", 31UL, 32UL);
            string sentinel = Path.Combine(root, "keep.txt");
            await File.WriteAllTextAsync(sentinel, "keep");
            string foreign = Path.Combine(root, "11111111111111111111111111111111.db");
            await File.WriteAllTextAsync(foreign, "not a save");

            await store.DeleteAsync(created.SaveId);

            File.Exists(store.GetSaveFilePath(created.SaveId)).ShouldBeFalse();
            File.Exists(sentinel).ShouldBeTrue();
            File.Exists(foreign).ShouldBeTrue();
            (await store.ListAsync()).Count.ShouldBe(0);
            await Should.ThrowAsync<SaveNotFoundException>(() => store.OpenAsync(created.SaveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenAsync_UnknownId_ThrowsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            await Should.ThrowAsync<SaveNotFoundException>(() => store.OpenAsync(Guid.NewGuid()));
            await Should.ThrowAsync<SaveNotFoundException>(() => store.DeleteAsync(Guid.NewGuid()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_InvalidName_ThrowsArgument(string name)
    {
        var (store, root) = CreateStore();
        try
        {
            await Should.ThrowAsync<ArgumentException>(() => store.CreateAsync(name, 1UL, 2UL));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_NameTooLong_ThrowsArgument()
    {
        var (store, root) = CreateStore();
        try
        {
            string longName = new('N', SaveStore.MaxNameLength + 1);
            await Should.ThrowAsync<ArgumentException>(() => store.CreateAsync(longName, 1UL, 2UL));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RulesSnapshotDocument_RoundTripsDeterministically()
    {
        RulesV1 rules = RulesV1.CreateDefault();
        string first = RulesSnapshotDocument.FromRules(rules).ToJson();
        string second = RulesSnapshotDocument.FromRules(rules).ToJson();
        second.ShouldBe(first);

        RulesV1 restored = RulesSnapshotDocument.FromJson(first).ToRules();
        Should.NotThrow(() => restored.Validate());
        restored.LeagueSize.ShouldBe(32);
        restored.ScoringTable[0].ShouldBe(77);
        restored.RoundBonusThousandths[0].ShouldBe(100);
        restored.BonusAgeWeightsThousandths.ShouldBe([1000, 800, 600, 400, 200, 0]);
        restored.CupBonusWeightPermille.ShouldBe(350);
    }

    [Fact]
    public void SaveFileNaming_RejectsArbitraryFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "saves-naming");
        Guid saveId = Guid.NewGuid();
        string fileName = $"{saveId:N}.db";

        SaveFileNaming.ResolveAndValidate(root, fileName).ShouldBe(Path.GetFullPath(Path.Combine(root, fileName)));
        SaveFileNaming.IsValidSaveFileName(fileName, out Guid parsed).ShouldBeTrue();
        parsed.ShouldBe(saveId);

        SaveFileNaming.IsValidSaveFileName($"{saveId:D}.db", out _).ShouldBeFalse();
        SaveFileNaming.IsValidSaveFileName("notes.txt", out _).ShouldBeFalse();
        SaveFileNaming.IsValidSaveFileName("save.db-wal", out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => SaveFileNaming.ResolveAndValidate(root, "../evil.db"));
        Should.Throw<ArgumentException>(() => SaveFileNaming.ResolveAndValidate(root, "evil.db"));
        Should.Throw<ArgumentException>(() => SaveFileNaming.ResolveAndValidate(root, "notes.txt"));
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
    }

    private static Pcg32State AdvanceOnce(ulong seed, ulong stream)
    {
        Pcg32V1 rng = new(seed, stream);
        _ = rng.NextUInt32();
        return rng.Snapshot();
    }

    private static async Task<string> ReadJournalModeAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        await context.Database.OpenConnectionAsync().ConfigureAwait(false);
        using System.Data.Common.DbCommand command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result?.ToString() ?? string.Empty;
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
