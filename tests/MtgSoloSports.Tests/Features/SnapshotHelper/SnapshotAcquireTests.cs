using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.SnapshotHelper;

/// <summary>
/// MSS-133: the snapshot helper acquires one verified working snapshot for an
/// explicitly configured save. Failed downloads or validations never replace
/// the previous valid snapshot. History-heavy coverage uses the shared
/// Season 1 template; every other test builds only the cheap fresh universe
/// it needs (creation plus export is the subject setup, not incidental history).
/// </summary>
public sealed class SnapshotAcquireTests
{
    [Fact]
    public async Task Acquire_FromHttp_Succeeds_AndWritesVerifiedSnapshot()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-http-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-cfg-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Http Save", 101UL, 202UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            SnapshotTestSupport.StubHandler stub = new(request => ExportResponse(request, created.Detail.SaveId, exported.ZipBytes));
            string configPath = SnapshotTestSupport.WriteConfig(configDir, created.Detail.SaveId);

            (int exit, string stdout, string stderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath], stub);

            exit.ShouldBe(0);
            stderr.ShouldContain("published working snapshot");
            SaveSnapshot.SnapshotInfo info = SaveSnapshot.SnapshotInfo.FromJson(stdout);
            info.SaveId.ShouldBe(created.Detail.SaveId);
            info.SaveName.ShouldBe("Http Save");
            info.SchemaVersion.ShouldBe(SaveSchemaVersion.Current);
            info.RulesVersion.ShouldBe(created.Detail.RulesVersion);
            info.ExportedUtc.ShouldBe(exported.Manifest.ExportedUtc);
            info.AcquiredUtc.ShouldBeGreaterThanOrEqualTo(info.ExportedUtc);
            string databasePath = SaveSnapshot.SnapshotReader.DatabasePathFor(Path.Combine(configDir, "snapshot-data"));
            File.Exists(databasePath).ShouldBeTrue();
            File.Exists(SaveSnapshot.SnapshotReader.ManifestPathFor(Path.Combine(configDir, "snapshot-data"))).ShouldBeTrue();
            info.DatabaseSha256.ShouldBe(exported.Manifest.DatabaseSha256);
            SaveBundle.ComputeFileSha256Hex(databasePath).ShouldBe(exported.Manifest.DatabaseSha256);
            info.Seasons.Count.ShouldBeGreaterThanOrEqualTo(1);
            Directory.GetDirectories(Path.Combine(configDir, "snapshot-data"), ".staging-*").ShouldBeEmpty();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Acquire_OfflineBundle_Succeeds()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-offline-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-cfg-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Offline Save", 303UL, 404UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            string bundlePath = Path.Combine(configDir, "offline.mtgsave.zip");
            await File.WriteAllBytesAsync(bundlePath, exported.ZipBytes);
            string configPath = SnapshotTestSupport.WriteConfig(configDir, created.Detail.SaveId);

            (int exit, string stdout, _) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath, "--bundle", bundlePath], handler: null);

            exit.ShouldBe(0);
            SaveSnapshot.SnapshotInfo info = SaveSnapshot.SnapshotInfo.FromJson(stdout);
            info.SaveId.ShouldBe(created.Detail.SaveId);
            SaveBundle.ComputeFileSha256Hex(info.SnapshotPath).ShouldBe(exported.Manifest.DatabaseSha256);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Acquire_WrongSaveId_IsRejected_AndPreservesPreviousSnapshot()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-identity-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-cfg-");
        try
        {
            SaveStore.CreationRecord first = await store.CreateAsync("First", 505UL, 606UL, UniverseTestCatalog.Build());
            SaveStore.CreationRecord second = await store.CreateAsync("Second", 707UL, 808UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord firstExport = await store.ExportAsync(first.Detail.SaveId);
            SaveStore.ExportRecord secondExport = await store.ExportAsync(second.Detail.SaveId);
            string configPath = SnapshotTestSupport.WriteConfig(configDir, first.Detail.SaveId);

            (int firstExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["acquire", "--config", configPath, "--bundle", await SnapshotTestSupport.WriteBundleAsync(configDir, "first.zip", firstExport.ZipBytes)],
                handler: null);
            firstExit.ShouldBe(0);
            string workingDir = Path.Combine(configDir, "snapshot-data");
            string infoBefore = await File.ReadAllTextAsync(SaveSnapshot.SnapshotReader.InfoPathFor(workingDir));
            string hashBefore = SaveBundle.ComputeFileSha256Hex(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir));

            (int exit, _, string stderr) = await SnapshotTestSupport.RunCliAsync(
                ["acquire", "--config", configPath, "--bundle", await SnapshotTestSupport.WriteBundleAsync(configDir, "second.zip", secondExport.ZipBytes)],
                handler: null);

            exit.ShouldBe(1);
            stderr.ShouldContain("does not match the configured save id");
            (await File.ReadAllTextAsync(SaveSnapshot.SnapshotReader.InfoPathFor(workingDir))).ShouldBe(infoBefore);
            SaveBundle.ComputeFileSha256Hex(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir)).ShouldBe(hashBefore);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Acquire_TamperedChecksum_IsRejected_AndPreservesPreviousSnapshot()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-tamper-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-cfg-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Victim", 909UL, 910UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            string configPath = SnapshotTestSupport.WriteConfig(configDir, created.Detail.SaveId);
            (int firstExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["acquire", "--config", configPath, "--bundle", await SnapshotTestSupport.WriteBundleAsync(configDir, "good.zip", exported.ZipBytes)],
                handler: null);
            firstExit.ShouldBe(0);
            string workingDir = Path.Combine(configDir, "snapshot-data");
            string hashBefore = SaveBundle.ComputeFileSha256Hex(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir));

            (SaveBundleManifest manifest, byte[] databaseBytes) = SnapshotTestSupport.Unpack(exported.ZipBytes);
            databaseBytes[200] ^= 0xFF;
            string tamperedPath = await SnapshotTestSupport.WriteBundleAsync(configDir, "tampered.zip", SnapshotTestSupport.Repack(manifest, databaseBytes));

            (int exit, _, string stderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath, "--bundle", tamperedPath], handler: null);

            exit.ShouldBe(1);
            stderr.ShouldContain("checksum");
            SaveBundle.ComputeFileSha256Hex(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir)).ShouldBe(hashBefore);
            SaveSnapshot.SnapshotReader.ReadInfo(workingDir).SaveId.ShouldBe(created.Detail.SaveId);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Acquire_UnsafeArchiveShape_IsRejected()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-shape-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-cfg-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Shaped", 911UL, 912UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            string configPath = SnapshotTestSupport.WriteConfig(configDir, created.Detail.SaveId);
            string workingDir = Path.Combine(configDir, "snapshot-data");
            (SaveBundleManifest manifest, byte[] databaseBytes) = SnapshotTestSupport.Unpack(exported.ZipBytes);

            string extraPath = await SnapshotTestSupport.WriteBundleAsync(configDir, "extra.zip", RepackWithExtra(manifest, databaseBytes));
            (int extraExit, _, string extraStderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath, "--bundle", extraPath], handler: null);
            extraExit.ShouldBe(1);
            extraStderr.ShouldContain("exactly 2 entries");

            string traversalPath = await SnapshotTestSupport.WriteBundleAsync(configDir, "traversal.zip", RepackWithTraversal(databaseBytes));
            (int traversalExit, _, string traversalStderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath, "--bundle", traversalPath], handler: null);
            traversalExit.ShouldBe(1);
            traversalStderr.ShouldContain("unexpected entry");

            File.Exists(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir)).ShouldBeFalse();
            File.Exists(SaveSnapshot.SnapshotReader.InfoPathFor(workingDir)).ShouldBeFalse();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Acquire_NotZip_IsRejected()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-notzip-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-cfg-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Keepsake", 913UL, 914UL, UniverseTestCatalog.Build());
            string configPath = SnapshotTestSupport.WriteConfig(configDir, created.Detail.SaveId);
            string garbagePath = await SnapshotTestSupport.WriteBundleAsync(configDir, "garbage.zip", Encoding.UTF8.GetBytes("this is not a save bundle"));

            (int exit, _, string stderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath, "--bundle", garbagePath], handler: null);

            exit.ShouldBe(1);
            stderr.ShouldContain("not a valid save bundle");
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Acquire_UnsupportedVersions_AreRejected_WithoutPublishing()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-versions-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-cfg-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Versioned", 915UL, 916UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            string configPath = SnapshotTestSupport.WriteConfig(configDir, created.Detail.SaveId);
            string workingDir = Path.Combine(configDir, "snapshot-data");
            (SaveBundleManifest manifest, byte[] databaseBytes) = SnapshotTestSupport.Unpack(exported.ZipBytes);

            string futureSchema = await SnapshotTestSupport.WriteBundleAsync(
                configDir, "future-schema.zip", SnapshotTestSupport.Repack(manifest with { SchemaVersion = SaveSchemaVersion.Current + 1 }, databaseBytes));
            (int schemaExit, _, string schemaStderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath, "--bundle", futureSchema], handler: null);
            schemaExit.ShouldBe(1);
            schemaStderr.ShouldContain("newer than supported");

            string futureRules = await SnapshotTestSupport.WriteBundleAsync(
                configDir, "future-rules.zip", SnapshotTestSupport.Repack(manifest with { RulesVersion = 999 }, databaseBytes));
            (int rulesExit, _, string rulesStderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath, "--bundle", futureRules], handler: null);
            rulesExit.ShouldBe(1);
            rulesStderr.ShouldContain("rules version");

            string badRng = await SnapshotTestSupport.WriteBundleAsync(
                configDir, "bad-rng.zip", SnapshotTestSupport.Repack(manifest with { RngAlgorithm = "Nope", RngVersion = 99 }, databaseBytes));
            (int rngExit, _, string rngStderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath, "--bundle", badRng], handler: null);
            rngExit.ShouldBe(1);
            rngStderr.ShouldContain("RNG");

            File.Exists(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir)).ShouldBeFalse();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Acquire_HttpFailures_ReturnActionableErrors_WithoutPublishing()
    {
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-cfg-");
        try
        {
            Guid saveId = Guid.NewGuid();
            string configPath = SnapshotTestSupport.WriteConfig(configDir, saveId);
            string workingDir = Path.Combine(configDir, "snapshot-data");

            SnapshotTestSupport.StubHandler notFound = new(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            (int missingExit, _, string missingStderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath], notFound);
            missingExit.ShouldBe(1);
            missingStderr.ShouldContain("was not found");

            SnapshotTestSupport.StubHandler broken = new(_ => throw new HttpRequestException("Connection refused"));
            (int downExit, _, string downStderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath], broken);
            downExit.ShouldBe(1);
            downStderr.ShouldContain("is unavailable");

            SnapshotTestSupport.StubHandler failing = new(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            (int errorExit, _, string errorStderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath], failing);
            errorExit.ShouldBe(1);
            errorStderr.ShouldContain("failed with 500");

            File.Exists(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir)).ShouldBeFalse();
        }
        finally
        {
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task Acquire_InterruptedRefresh_PreservesValidSnapshot()
    {
        var (store, root) = TestSaveStores.CreateStore("mtgsolosports-snapshot-interrupt-");
        string configDir = SnapshotTestSupport.NewTempDir("mtgsolosports-snapshot-cfg-");
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync("Steady", 917UL, 918UL, UniverseTestCatalog.Build());
            SaveStore.ExportRecord exported = await store.ExportAsync(created.Detail.SaveId);
            string configPath = SnapshotTestSupport.WriteConfig(configDir, created.Detail.SaveId);
            (int firstExit, _, _) = await SnapshotTestSupport.RunCliAsync(
                ["acquire", "--config", configPath, "--bundle", await SnapshotTestSupport.WriteBundleAsync(configDir, "good.zip", exported.ZipBytes)],
                handler: null);
            firstExit.ShouldBe(0);
            string workingDir = Path.Combine(configDir, "snapshot-data");
            string infoBefore = await File.ReadAllTextAsync(SaveSnapshot.SnapshotReader.InfoPathFor(workingDir));
            string hashBefore = SaveBundle.ComputeFileSha256Hex(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir));

            byte[] truncated = exported.ZipBytes[..(exported.ZipBytes.Length / 2)];
            SnapshotTestSupport.StubHandler stub = new(request => ExportResponse(request, created.Detail.SaveId, truncated));
            (int exit, _, string stderr) = await SnapshotTestSupport.RunCliAsync(["acquire", "--config", configPath], stub);

            exit.ShouldBe(1);
            stderr.ShouldContain("not a valid save bundle");
            (await File.ReadAllTextAsync(SaveSnapshot.SnapshotReader.InfoPathFor(workingDir))).ShouldBe(infoBefore);
            SaveBundle.ComputeFileSha256Hex(SaveSnapshot.SnapshotReader.DatabasePathFor(workingDir)).ShouldBe(hashBefore);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
            TestSaveStores.DeleteRoot(configDir);
        }
    }

    [Fact]
    public async Task ListSaves_ReturnsSaves_WithoutSelecting()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        SnapshotTestSupport.StubHandler stub = new(request =>
        {
            if (string.Equals(request.RequestUri?.AbsolutePath, "/api/saves", StringComparison.Ordinal))
            {
                string json = JsonSerializer.Serialize(new
                {
                    saves = new[]
                    {
                        new { saveId = first, name = "Alpha", createdUtc = "2026-01-01T00:00:00Z", schemaVersion = 1, currentSeason = 2, phase = "SeasonInProgress" },
                        new { saveId = second, name = "Alpha", createdUtc = "2026-02-01T00:00:00Z", schemaVersion = 1, currentSeason = 1, phase = "SeasonInProgress" },
                    },
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        (int exit, string stdout, string stderr) = await SnapshotTestSupport.RunCliAsync(["list-saves", "--base-url", "http://localhost:8080"], stub);

        exit.ShouldBe(0);
        stdout.ShouldContain(first.ToString("D"));
        stdout.ShouldContain(second.ToString("D"));
        stderr.ShouldContain("nothing is selected automatically");

        (int usageExit, _, _) = await SnapshotTestSupport.RunCliAsync(["acquire"], handler: null);
        usageExit.ShouldBe(2);
    }

    private static HttpResponseMessage ExportResponse(HttpRequestMessage request, Guid saveId, byte[] bundle)
    {
        if (string.Equals(request.RequestUri?.AbsolutePath, $"/api/saves/{saveId:D}/export", StringComparison.Ordinal))
        {
            ByteArrayContent content = new(bundle);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static byte[] RepackWithExtra(SaveBundleManifest manifest, byte[] databaseBytes)
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry databaseEntry = archive.CreateEntry(SaveBundleManifest.DatabaseEntryName);
            using (Stream stream = databaseEntry.Open())
            {
                stream.Write(databaseBytes, 0, databaseBytes.Length);
            }

            byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJson());
            ZipArchiveEntry manifestEntry = archive.CreateEntry(SaveBundleManifest.ManifestEntryName);
            using (Stream stream = manifestEntry.Open())
            {
                stream.Write(manifestBytes, 0, manifestBytes.Length);
            }

            ZipArchiveEntry extra = archive.CreateEntry("notes.txt");
            using (Stream stream = extra.Open())
            {
                stream.Write(Encoding.UTF8.GetBytes("extra"));
            }
        }

        return buffer.ToArray();
    }

    private static byte[] RepackWithTraversal(byte[] databaseBytes)
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry databaseEntry = archive.CreateEntry(SaveBundleManifest.DatabaseEntryName);
            using (Stream stream = databaseEntry.Open())
            {
                stream.Write(databaseBytes, 0, databaseBytes.Length);
            }

            ZipArchiveEntry traversal = archive.CreateEntry("../evil.txt");
            using (Stream stream = traversal.Open())
            {
                stream.Write(Encoding.UTF8.GetBytes("evil"));
            }
        }

        return buffer.ToArray();
    }
}
