using System.Net;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.SnapshotHelper;

/// <summary>
/// MSS-136: large saves work by default with no fixed bundle/database ceiling.
/// A valid database and bundle above the former 256 MiB thresholds acquire
/// through the real production paths (file export, offline CLI, HTTP download
/// without Content-Length, file extraction) with bounded buffers and no
/// whole-artifact byte arrays. Temporary filler (SQLite randomblob, not
/// sporting RNG) inflates size; no seasons are simulated and no large
/// artifacts are committed. Failed large transfers preserve the prior
/// snapshot and clean temp resources; source sporting state and RNG never move.
/// </summary>
public sealed class SnapshotLargeSaveTests
{
    private const long FormerLimit = 256L * 1024L * 1024L;
    private const int FillerRows = 70;
    private const int FillerBlobBytes = 4_000_000;

    [Fact]
    public async Task LargeSave_OfflineAcquire_Succeeds_AndQueries()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-largesave-offline-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-largesave-cfg-");
        try
        {
            LargeSaveArtifacts artifacts = await BuildLargeArtifactsAsync(store, "Large Offline", configDir, "large.mtgsave.zip");
            await AssertOfflineAcquireAsync(artifacts, configDir);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task LargeSave_HttpWithoutContentLength_Succeeds_AndFailedPreserves()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-largesave-http-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-largesave-cfg-");
        try
        {
            LargeSaveArtifacts artifacts = await BuildLargeArtifactsAsync(store, "Large Http", configDir, "large-http.mtgsave.zip");
            await AssertHttpAcquireAndPreservationAsync(store, artifacts, configDir);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    private sealed record LargeSaveArtifacts(
        SaveStore Store,
        Guid SaveId,
        string LivePath,
        string LiveShaBeforeExport,
        SaveSnapshot.SnapshotReader.RngView RngBeforeExport,
        string BundlePath,
        SaveStore.ExportFileRecord Exported);

    private static async Task<LargeSaveArtifacts> BuildLargeArtifactsAsync(
        SaveStore store, string saveName, string configDir, string bundleFileName)
    {
        SaveStore.CreationRecord created = await store.CreateAsync(
            saveName, 4242UL, 777UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        string livePath = store.GetSaveFilePath(created.Detail.SaveId);
        var rngBeforeFiller = SaveSnapshot.SnapshotReader.ReadRngState(livePath);

        await InflateWithFillerAsync(livePath, FillerRows, FillerBlobBytes).ConfigureAwait(false);

        SaveSnapshot.SnapshotReader.ReadRngState(livePath).ShouldBe(rngBeforeFiller);
        string liveShaBeforeExport = SaveBundle.ComputeFileSha256Hex(livePath);
        var rngBeforeExport = SaveSnapshot.SnapshotReader.ReadRngState(livePath);
        new FileInfo(livePath).Length.ShouldBeGreaterThan(FormerLimit);

        string bundlePath = Path.Combine(configDir, bundleFileName);
        SaveStore.ExportFileRecord exported = await store
            .ExportToFileAsync(created.Detail.SaveId, bundlePath).ConfigureAwait(false);
        new FileInfo(bundlePath).Length.ShouldBeGreaterThan(FormerLimit);

        return new LargeSaveArtifacts(
            store, created.Detail.SaveId, livePath, liveShaBeforeExport, rngBeforeExport, bundlePath, exported);
    }

    private static async Task AssertOfflineAcquireAsync(LargeSaveArtifacts artifacts, string configDir)
    {
        string configPath = SnapshotTestSupport.WriteConfig(configDir, artifacts.SaveId);
        (int exit, string stdout, _) = await SnapshotTestSupport.RunCliAsync(
            ["acquire", "--config", configPath, "--bundle", artifacts.BundlePath], handler: null).ConfigureAwait(false);

        exit.ShouldBe(0);
        SaveSnapshot.SnapshotInfo info = SaveSnapshot.SnapshotInfo.FromJson(stdout);
        info.SaveId.ShouldBe(artifacts.SaveId);
        info.DatabaseSha256.ShouldBe(artifacts.Exported.Manifest.DatabaseSha256);

        string workingDir = Path.Combine(configDir, "snapshot-data");
        new FileInfo(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir))
            .Length.ShouldBeGreaterThan(FormerLimit);
        SaveBundle.ComputeFileSha256Hex(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir))
            .ShouldBe(artifacts.Exported.Manifest.DatabaseSha256);

        SaveSnapshot.SnapshotReader.QueryResult athletes = await SaveSnapshot.SnapshotReader.QueryAsync(
            info.SnapshotPath, "SELECT COUNT(*) AS AthleteCount FROM SaveAthletes", 10).ConfigureAwait(false);
        athletes.Rows.Single().Single().ShouldBe(2048L);

        SaveSnapshot.SnapshotReader.QueryResult filler = await SaveSnapshot.SnapshotReader.QueryAsync(
            info.SnapshotPath, "SELECT COUNT(*) AS FillerRows FROM LargeFiller", 10).ConfigureAwait(false);
        filler.Rows.Single().Single().ShouldBe((long)FillerRows);

        SaveBundle.ComputeFileSha256Hex(artifacts.LivePath).ShouldBe(artifacts.LiveShaBeforeExport);
        SaveSnapshot.SnapshotReader.ReadRngState(artifacts.LivePath).ShouldBe(artifacts.RngBeforeExport);
        Directory.GetDirectories(workingDir, ".staging-*").ShouldBeEmpty();
        Directory.GetFiles(workingDir, "*.new").ShouldBeEmpty();
    }

    private static async Task AssertHttpAcquireAndPreservationAsync(
        SaveStore store, LargeSaveArtifacts artifacts, string configDir)
    {
        string configPath = SnapshotTestSupport.WriteConfig(configDir, artifacts.SaveId);
        SnapshotTestSupport.StubHandler noLength = new(request =>
            ExportFileResponseWithoutContentLength(request, artifacts.SaveId, artifacts.BundlePath));
        (int exit, string stdout, _) = await SnapshotTestSupport.RunCliAsync(
            ["acquire", "--config", configPath], noLength).ConfigureAwait(false);

        exit.ShouldBe(0);
        SaveSnapshot.SnapshotInfo info = SaveSnapshot.SnapshotInfo.FromJson(stdout);
        info.DatabaseSha256.ShouldBe(artifacts.Exported.Manifest.DatabaseSha256);

        string workingDir = Path.Combine(configDir, "snapshot-data");
        string infoBefore = await File.ReadAllTextAsync(
            SaveSnapshot.SnapshotReader.InfoPathFor(workingDir)).ConfigureAwait(false);
        string hashBefore = SaveBundle.ComputeFileSha256Hex(
            SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir));

        SaveSnapshot.SnapshotReader.QueryResult seasons = await SaveSnapshot.SnapshotReader.QueryAsync(
            info.SnapshotPath,
            "SELECT SeasonNumber, HasSuperleague, IsComplete FROM Seasons ORDER BY SeasonNumber;",
            10).ConfigureAwait(false);
        seasons.Rows.Count.ShouldBeGreaterThanOrEqualTo(1);

        await AssertTruncatedPreservesAsync(configPath, artifacts, workingDir, infoBefore, hashBefore).ConfigureAwait(false);
        await AssertWrongIdentityPreservesAsync(store, configPath, configDir, workingDir, hashBefore).ConfigureAwait(false);

        SaveBundle.ComputeFileSha256Hex(artifacts.LivePath).ShouldBe(artifacts.LiveShaBeforeExport);
        SaveSnapshot.SnapshotReader.ReadRngState(artifacts.LivePath).ShouldBe(artifacts.RngBeforeExport);
    }

    private static async Task AssertTruncatedPreservesAsync(
        string configPath, LargeSaveArtifacts artifacts, string workingDir, string infoBefore, string hashBefore)
    {
        byte[] bundleFileBytes = await File.ReadAllBytesAsync(artifacts.BundlePath).ConfigureAwait(false);
        byte[] truncated = bundleFileBytes[..(bundleFileBytes.Length / 2)];
        SnapshotTestSupport.StubHandler truncatedStub = new(request =>
            ExportBytesResponse(request, artifacts.SaveId, truncated));
        (int failExit, _, string failStderr) = await SnapshotTestSupport.RunCliAsync(
            ["acquire", "--config", configPath], truncatedStub).ConfigureAwait(false);

        failExit.ShouldBe(1);
        failStderr.ShouldNotBeNullOrWhiteSpace();
        (await File.ReadAllTextAsync(
            SaveSnapshot.SnapshotReader.InfoPathFor(workingDir)).ConfigureAwait(false)).ShouldBe(infoBefore);
        SaveBundle.ComputeFileSha256Hex(
            SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir)).ShouldBe(hashBefore);
        Directory.GetDirectories(workingDir, ".staging-*").ShouldBeEmpty();
        Directory.GetFiles(workingDir, "*.new").ShouldBeEmpty();
    }

    private static async Task AssertWrongIdentityPreservesAsync(
        SaveStore store, string configPath, string configDir, string workingDir, string hashBefore)
    {
        SaveStore.CreationRecord other = await store.CreateAsync(
            "Other", 111UL, 222UL, UniverseTestCatalog.Build()).ConfigureAwait(false);
        string otherBundlePath = Path.Combine(configDir, "other.mtgsave.zip");
        _ = await store.ExportToFileAsync(other.Detail.SaveId, otherBundlePath).ConfigureAwait(false);
        (int identityExit, _, string identityStderr) = await SnapshotTestSupport.RunCliAsync(
            ["acquire", "--config", configPath, "--bundle", otherBundlePath], handler: null).ConfigureAwait(false);
        identityExit.ShouldBe(1);
        identityStderr.ShouldContain("does not match the configured save id");
        SaveBundle.ComputeFileSha256Hex(
            SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir)).ShouldBe(hashBefore);
    }

    private static async Task InflateWithFillerAsync(string livePath, int rowCount, int blobBytes)
    {
        // Temporary size filler only: SQLite-generated random bytes in a side
        // table. Not sporting randomness, never consumed as game RNG, and
        // ignored by snapshot validation (identity/schema/rules/RNG still match).
        SqliteConnection.ClearAllPools();
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = livePath,
            Pooling = false,
        };
        using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        using (SqliteCommand create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS LargeFiller (Id INTEGER PRIMARY KEY, Payload BLOB NOT NULL); DELETE FROM LargeFiller;";
            await create.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using (SqliteCommand begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN;";
            await begin.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        for (int i = 0; i < rowCount; i++)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = $"INSERT INTO LargeFiller (Payload) VALUES (randomblob({blobBytes}));";
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using (SqliteCommand commit = connection.CreateCommand())
        {
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using (SqliteCommand checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();
    }

    private static HttpResponseMessage ExportFileResponseWithoutContentLength(HttpRequestMessage request, Guid saveId, string bundlePath)
    {
        if (string.Equals(request.RequestUri?.AbsolutePath, $"/api/saves/{saveId:D}/export", StringComparison.Ordinal))
        {
            NoContentLengthFileContent content = new(bundlePath);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage ExportBytesResponse(HttpRequestMessage request, Guid saveId, byte[] bundle)
    {
        if (string.Equals(request.RequestUri?.AbsolutePath, $"/api/saves/{saveId:D}/export", StringComparison.Ordinal))
        {
            ByteArrayContent content = new(bundle);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Streams a bundle file without a known length, proving the downloader
    /// does not rely on Content-Length.
    /// </summary>
    private sealed class NoContentLengthFileContent : HttpContent
    {
        private readonly string _bundlePath;

        public NoContentLengthFileContent(string bundlePath)
        {
            _bundlePath = bundlePath;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using FileStream source = new(
                _bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
