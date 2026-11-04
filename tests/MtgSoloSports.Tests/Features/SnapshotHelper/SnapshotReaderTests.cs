using System.Text.Json;
using Microsoft.Data.Sqlite;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.SnapshotHelper;

/// <summary>
/// MSS-133: all inspection of a published snapshot uses read-only SQLite access
/// with no bootstrap, migration, seeding or simulation. Reads never alter
/// sporting state, RNG, the source bundle, the manifest or the validated
/// database, and a torn publish is reported instead of served as current.
/// </summary>
public sealed class SnapshotReaderTests
{
    private static readonly DateTimeOffset FixedAcquiredUtc = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Query_Select_Works_WithoutModifyingSnapshot()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-query-");
        string workingDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-work-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Queried", 111UL, 222UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            SaveSnapshot.SnapshotInfo info = await SaveSnapshot.SnapshotAcquirer.AcquireFromBundleAsync(
                exported.ZipBytes, created.Detail.SaveId, workingDir, FixedAcquiredUtc);
            info.AcquiredUtc.ShouldBe(FixedAcquiredUtc);
            string databasePath = info.SnapshotPath;

            string hashBefore = SaveBundle.ComputeFileSha256Hex(databasePath);
            SaveSnapshot.SnapshotReader.RngView rngBefore = SaveSnapshot.SnapshotReader.ReadRngState(databasePath);
            byte[] bundleHashBefore = exported.ZipBytes;

            SaveSnapshot.SnapshotReader.QueryResult athletes = await SaveSnapshot.SnapshotReader.QueryAsync(
                databasePath, "SELECT COUNT(*) AS AthleteCount FROM SaveAthletes", 10);
            athletes.Rows.Single().Single().ShouldBe(2048L);

            SaveSnapshot.SnapshotReader.QueryResult seasons = await SaveSnapshot.SnapshotReader.QueryAsync(
                databasePath, "SELECT SeasonNumber, IsComplete FROM Seasons ORDER BY SeasonNumber", 10);
            seasons.Rows.Count.ShouldBe(1);
            seasons.Rows[0][0].ShouldBe(1L);

            SaveBundle.ComputeFileSha256Hex(databasePath).ShouldBe(hashBefore);
            SaveSnapshot.SnapshotReader.ReadRngState(databasePath).ShouldBe(rngBefore);
            SaveBundle.ComputeBytesSha256Hex(bundleHashBefore).ShouldBe(SaveBundle.ComputeBytesSha256Hex(exported.ZipBytes));
            AssertNoWriteFrames(databasePath);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(workingDir);
        }
    }

    [Fact]
    public async Task Query_WriteAttempt_Fails_ReadOnly_AndLeavesSnapshotUntouched()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-readonly-");
        string workingDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-work-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Guarded", 333UL, 444UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            SaveSnapshot.SnapshotInfo info = await SaveSnapshot.SnapshotAcquirer.AcquireFromBundleAsync(
                exported.ZipBytes, created.Detail.SaveId, workingDir, FixedAcquiredUtc);
            string hashBefore = SaveBundle.ComputeFileSha256Hex(info.SnapshotPath);

            await Should.ThrowAsync<ArgumentException>(() => SaveSnapshot.SnapshotReader.QueryAsync(
                info.SnapshotPath, "INSERT INTO SaveMetadata (Id) VALUES (1)", 10));

            SqliteException writeFailure = await Should.ThrowAsync<SqliteException>(() => WriteThroughReadOnlyAsync(info.SnapshotPath));
            writeFailure.Message.ShouldContain("readonly", Case.Insensitive);

            SaveBundle.ComputeFileSha256Hex(info.SnapshotPath).ShouldBe(hashBefore);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(workingDir);
        }
    }

    [Fact]
    public async Task Query_RejectsNonSelectStatements()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-reject-");
        string workingDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-work-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Strict", 555UL, 666UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            SaveSnapshot.SnapshotInfo info = await SaveSnapshot.SnapshotAcquirer.AcquireFromBundleAsync(
                exported.ZipBytes, created.Detail.SaveId, workingDir, FixedAcquiredUtc);
            string[] forbidden =
            [
                "DELETE FROM SaveAthletes",
                "UPDATE SaveMetadata SET Name = 'x' WHERE Id = 1",
                "DROP TABLE Seasons",
                "PRAGMA journal_mode=DELETE",
                "SELECT 1; DELETE FROM SaveAthletes",
                "WITH x AS (SELECT 1) DELETE FROM SaveAthletes",
            ];
            foreach (string sql in forbidden)
            {
                await Should.ThrowAsync<ArgumentException>(() => SaveSnapshot.SnapshotReader.QueryAsync(info.SnapshotPath, sql, 10));
            }
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(workingDir);
        }
    }

    [Fact]
    public async Task Query_RespectsLimit_AndFlagsTruncation()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-limit-");
        string workingDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-work-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Bounded", 777UL, 888UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            SaveSnapshot.SnapshotInfo info = await SaveSnapshot.SnapshotAcquirer.AcquireFromBundleAsync(
                exported.ZipBytes, created.Detail.SaveId, workingDir, FixedAcquiredUtc);

            SaveSnapshot.SnapshotReader.QueryResult result = await SaveSnapshot.SnapshotReader.QueryAsync(
                info.SnapshotPath, "SELECT Name FROM SaveAthletes ORDER BY Name", 5);

            result.Rows.Count.ShouldBe(5);
            result.Truncated.ShouldBeTrue();
            result.Columns.ShouldBe(["Name"]);

            await Should.ThrowAsync<ArgumentException>(() => SaveSnapshot.SnapshotReader.QueryAsync(
                info.SnapshotPath, "SELECT Name FROM SaveAthletes", SaveSnapshot.SnapshotReader.MaxQueryLimit + 1));
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(workingDir);
        }
    }

    [Fact]
    public async Task ReadInfo_RejectsTornPublish_InsteadOfServingStaleData()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-torn-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-cfg-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Torn", 999UL, 1000UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            string configPath = SnapshotTestSupport.WriteConfig(configDir, created.Detail.SaveId);
            (int exit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["acquire", "--config", configPath, "--bundle",
                    await SnapshotTestSupport.WriteBundleAsync(configDir, "good.zip", exported.ZipBytes)],
                handler: null);
            exit.ShouldBe(0);
            string workingDir = Path.Combine(configDir, "snapshot-data");
            string infoPath = SaveSnapshot.SnapshotReader.InfoPathFor(workingDir);
            using (JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(infoPath)))
            {
                string original = document.RootElement.GetRawText();
                string tampered = original.Replace(
                    exported.Manifest.DatabaseSha256.ToLowerInvariant(),
                    new string('0', 64),
                    StringComparison.Ordinal);
                string.Equals(tampered, original, StringComparison.Ordinal).ShouldBeFalse();
                await File.WriteAllTextAsync(infoPath, tampered);
            }

            Should.Throw<InvalidOperationException>(() => SaveSnapshot.SnapshotReader.ReadInfo(workingDir))
                .Message.ShouldContain("does not match");

            (int queryExit, _, string queryStderr) = await SnapshotTestSupport.RunCliAsync(
                ["query", "--config", configPath, "--sql", "SELECT COUNT(*) FROM SaveAthletes"], handler: null);
            queryExit.ShouldBe(1);
            queryStderr.ShouldContain("does not match");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Coverage_ReportsRealStageHistory()
    {
        // Stage coverage is the subject here, so the Season 1 template is the
        // state under test, forked into an isolated root per testing.md.
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-snapshot-coverage-");
        string workingDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-work-");
        try
        {
            SaveStore.ExportRecord exported = await store.ExportAsync(saveId);
            SaveSnapshot.SnapshotInfo info = await SaveSnapshot.SnapshotAcquirer.AcquireFromBundleAsync(
                exported.ZipBytes, saveId, workingDir, FixedAcquiredUtc);

            SaveSnapshot.SeasonCoverage season = info.Seasons.Single(s => s.SeasonNumber == 1);
            season.IsComplete.ShouldBeTrue();
            season.StageCount.ShouldBeGreaterThan(0);
            season.CompletedStages.ShouldBe(season.StageCount);
            season.RoundCount.ShouldBeGreaterThan(0);
            info.CurrentSeason.ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(workingDir);
        }
    }

    private static void AssertNoWriteFrames(string databasePath)
    {
        // Read-only inspection must never append frames: no rollback journal,
        // and any WAL bookkeeping left by the read stays empty.
        File.Exists(databasePath + "-journal").ShouldBeFalse();
        string wal = databasePath + "-wal";
        if (File.Exists(wal))
        {
            new FileInfo(wal).Length.ShouldBe(0L);
        }
    }

    private static async Task WriteThroughReadOnlyAsync(string databasePath)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA query_only=ON;";
        await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        using SqliteCommand write = connection.CreateCommand();
        write.CommandText = "INSERT INTO SaveMetadata (Id) VALUES (1);";
        await write.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
