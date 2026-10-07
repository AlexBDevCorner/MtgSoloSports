using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Saves;

/// <summary>
/// API-level integration tests for save portability and technical recovery:
/// export download, import round trip, overwrite conflicts, corrupt-payload
/// safety, and the checkpoint create/list/restore endpoints (which stay out
/// of the normal gameplay UI by design).
/// </summary>
public sealed class SavePortabilityApiTests
{
    [Fact]
    public async Task ExportDownload_ImportRoundTrip_ViaApi()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage created = await client.PostAsJsonAsync(
                "/api/saves",
                new { name = "Portable Universe", seed = 1001UL, stream = 2002UL });
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            CreatedPayload? createdPayload = await created.Content.ReadFromJsonAsync<CreatedPayload>();
            createdPayload.ShouldNotBeNull();

            using HttpResponseMessage exported = await client.GetAsync($"/api/saves/{createdPayload.SaveId:D}/export");
            exported.StatusCode.ShouldBe(HttpStatusCode.OK);
            exported.Content.Headers.ContentType?.MediaType.ShouldBe("application/zip");
            byte[] bundle = await exported.Content.ReadAsByteArrayAsync();
            bundle.Length.ShouldBeGreaterThan(0);
            Guid manifestSaveId = ReadManifestSaveId(bundle);
            manifestSaveId.ShouldBe(createdPayload.SaveId);

            using HttpResponseMessage deleted = await client.DeleteAsync($"/api/saves/{createdPayload.SaveId:D}");
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            using HttpResponseMessage imported = await PostBundleAsync(client, "/api/saves/import", bundle);
            imported.StatusCode.ShouldBe(HttpStatusCode.Created);
            DetailPayload? detail = await imported.Content.ReadFromJsonAsync<DetailPayload>();
            detail.ShouldNotBeNull();
            detail.SaveId.ShouldBe(createdPayload.SaveId);
            detail.Name.ShouldBe("Portable Universe");
            detail.SchemaVersion.ShouldBe(SaveSchemaVersion.Current);
            detail.RulesVersion.ShouldBe(MtgSoloSports.SimulationKernel.Rules.RulesV3.RulesVersion);
            detail.RngState.ShouldBe(createdPayload.RngState);
            detail.RngStream.ShouldBe(createdPayload.RngStream);

            using HttpResponseMessage listed = await client.GetAsync("/api/saves");
            ListPayload? listPayload = await listed.Content.ReadFromJsonAsync<ListPayload>();
            listPayload.ShouldNotBeNull();
            listPayload.Saves.Count.ShouldBe(1);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_Existing_ReturnsConflict_ThenOverwrite_Succeeds()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage created = await client.PostAsJsonAsync(
                "/api/saves",
                new { name = "Conflicted", seed = 3003UL, stream = 4004UL });
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            CreatedPayload? createdPayload = await created.Content.ReadFromJsonAsync<CreatedPayload>();
            createdPayload.ShouldNotBeNull();

            byte[] bundle = await DownloadBundleAsync(client, createdPayload.SaveId);

            using HttpResponseMessage conflicted = await PostBundleAsync(client, "/api/saves/import", bundle);
            conflicted.StatusCode.ShouldBe(HttpStatusCode.Conflict);

            using HttpResponseMessage overwritten = await PostBundleAsync(client, "/api/saves/import?overwrite=true", bundle);
            overwritten.StatusCode.ShouldBe(HttpStatusCode.Created);
            DetailPayload? detail = await overwritten.Content.ReadFromJsonAsync<DetailPayload>();
            detail.ShouldNotBeNull();
            detail.SaveId.ShouldBe(createdPayload.SaveId);

            using HttpResponseMessage checkpoints = await client.GetAsync($"/api/saves/{createdPayload.SaveId:D}/checkpoints");
            checkpoints.StatusCode.ShouldBe(HttpStatusCode.OK);
            CheckpointListPayload? checkpointList = await checkpoints.Content.ReadFromJsonAsync<CheckpointListPayload>();
            checkpointList.ShouldNotBeNull();
            checkpointList.Checkpoints.Count.ShouldBe(1);
            checkpointList.Checkpoints[0].Reason.ShouldBe("pre-import-overwrite");
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Import_Corrupt_ReturnsBadRequest_AndLeavesSavesUntouched()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage created = await client.PostAsJsonAsync(
                "/api/saves",
                new { name = "Untouched", seed = 5005UL, stream = 6006UL });
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            CreatedPayload? createdPayload = await created.Content.ReadFromJsonAsync<CreatedPayload>();
            createdPayload.ShouldNotBeNull();

            byte[] bundle = await DownloadBundleAsync(client, createdPayload.SaveId);
            byte[] truncated = bundle[..(bundle.Length / 2)];

            using HttpResponseMessage garbage = await PostBundleAsync(client, "/api/saves/import", "not a bundle"u8.ToArray());
            garbage.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            using HttpResponseMessage shortBundle = await PostBundleAsync(client, "/api/saves/import", truncated);
            shortBundle.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            using HttpResponseMessage listed = await client.GetAsync("/api/saves");
            ListPayload? listPayload = await listed.Content.ReadFromJsonAsync<ListPayload>();
            listPayload.ShouldNotBeNull();
            listPayload.Saves.Count.ShouldBe(1);

            using HttpResponseMessage opened = await client.GetAsync($"/api/saves/{createdPayload.SaveId:D}");
            opened.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Checkpoints_CreateListRestore_ViaApi()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage created = await client.PostAsJsonAsync(
                "/api/saves",
                new { name = "Recovery", seed = 7007UL, stream = 8008UL });
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            CreatedPayload? createdPayload = await created.Content.ReadFromJsonAsync<CreatedPayload>();
            createdPayload.ShouldNotBeNull();

            using HttpResponseMessage checkpointed = await client.PostAsJsonAsync(
                $"/api/saves/{createdPayload.SaveId:D}/checkpoints",
                new { reason = "api-probe" });
            checkpointed.StatusCode.ShouldBe(HttpStatusCode.Created);
            checkpointed.Headers.Location.ShouldNotBeNull();
            CheckpointPayload? checkpoint = await checkpointed.Content.ReadFromJsonAsync<CheckpointPayload>();
            checkpoint.ShouldNotBeNull();
            checkpoint.SaveId.ShouldBe(createdPayload.SaveId);
            checkpoint.Reason.ShouldBe("api-probe");

            using HttpResponseMessage listed = await client.GetAsync($"/api/saves/{createdPayload.SaveId:D}/checkpoints");
            listed.StatusCode.ShouldBe(HttpStatusCode.OK);
            CheckpointListPayload? checkpointList = await listed.Content.ReadFromJsonAsync<CheckpointListPayload>();
            checkpointList.ShouldNotBeNull();
            checkpointList.Checkpoints.Count.ShouldBe(1);

            using HttpContent empty = new StringContent(string.Empty);
            using HttpResponseMessage restored = await client.PostAsync(
                $"/api/saves/{createdPayload.SaveId:D}/checkpoints/{checkpoint.CheckpointId:D}/restore", empty);
            restored.StatusCode.ShouldBe(HttpStatusCode.OK);
            RestorePayload? restoredPayload = await restored.Content.ReadFromJsonAsync<RestorePayload>();
            restoredPayload.ShouldNotBeNull();
            restoredPayload.SaveId.ShouldBe(createdPayload.SaveId);
            restoredPayload.RestoredCheckpointId.ShouldBe(checkpoint.CheckpointId);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAndCheckpoint_UnknownSave_ReturnsNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid missing = Guid.NewGuid();

            using HttpResponseMessage exported = await client.GetAsync($"/api/saves/{missing:D}/export");
            exported.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage listed = await client.GetAsync($"/api/saves/{missing:D}/checkpoints");
            listed.StatusCode.ShouldBe(HttpStatusCode.OK);
            CheckpointListPayload? checkpointList = await listed.Content.ReadFromJsonAsync<CheckpointListPayload>();
            checkpointList.ShouldNotBeNull();
            checkpointList.Checkpoints.Count.ShouldBe(0);

            using HttpResponseMessage restored = await client.PostAsync(
                $"/api/saves/{missing:D}/checkpoints/{Guid.NewGuid():D}/restore",
                new StringContent(string.Empty));
            restored.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<byte[]> DownloadBundleAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage exported = await client.GetAsync($"/api/saves/{saveId:D}/export").ConfigureAwait(false);
        exported.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await exported.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> PostBundleAsync(HttpClient client, string url, byte[] bundle)
    {
        using ByteArrayContent content = new(bundle);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return await client.PostAsync(url, content).ConfigureAwait(false);
    }

    private static Guid ReadManifestSaveId(byte[] bundle)
    {
        using ZipArchive archive = new(new MemoryStream(bundle, writable: false), ZipArchiveMode.Read);
        ZipArchiveEntry manifestEntry = archive.Entries.Single(e => string.Equals(e.FullName, "manifest.json", StringComparison.Ordinal));
        using StreamReader reader = new(manifestEntry.Open(), Encoding.UTF8);
        using JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
        return document.RootElement.GetProperty("saveId").GetGuid();
    }

    private static async Task SeedCatalogAsync(HttpClient client)
    {
        string bulkJson = UniverseTestCatalog.BuildBulkJson();
        using StringContent content = new(bulkJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage imported = await client.PostAsync("/api/catalog/import", content).ConfigureAwait(false);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }

    private sealed record CreatedPayload(
        Guid SaveId,
        string Name,
        int SchemaVersion,
        string RngState,
        string RngStream);

    private sealed record DetailPayload(
        Guid SaveId,
        string Name,
        int SchemaVersion,
        int CurrentSeason,
        string Phase,
        int RulesVersion,
        string RngState,
        string RngStream);

    private sealed record ListEntry(Guid SaveId);

    private sealed record ListPayload(IReadOnlyList<ListEntry> Saves);

    private sealed record CheckpointPayload(
        Guid CheckpointId,
        Guid SaveId,
        string Reason);

    private sealed record CheckpointListPayload(IReadOnlyList<CheckpointPayload> Checkpoints);

    private sealed record RestorePayload(
        Guid SaveId,
        Guid RestoredCheckpointId);
}
