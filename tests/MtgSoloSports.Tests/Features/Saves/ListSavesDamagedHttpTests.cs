using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Saves;

/// <summary>
/// MSS-145 damaged-placeholder listing contract through the real pipeline:
/// a save file that exists on disk but cannot be read at all is returned as an
/// explicit unreadable placeholder (id plus file name and a short reason),
/// never silently omitted, while healthy saves keep listing normally.
/// </summary>
public sealed class ListSavesDamagedHttpTests
{
    [Fact]
    public async Task GarbageSaveFile_ListsAsUnreadablePlaceholderWithReasonBesideHealthySaves()
    {
        var (factory, root) = CreateFactory();
        try
        {
            string savesRoot = Path.Combine(root, "saves");
            Guid healthyId = await SharedSaveTemplates.SeedSeason1CompleteAsync(savesRoot);

            Guid damagedId = Guid.NewGuid();
            string damagedFileName = SaveFileNaming.GetSaveFileName(damagedId);
            await File.WriteAllTextAsync(
                Path.Combine(savesRoot, damagedFileName),
                "this is not a sqlite database at all");

            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync("/api/saves");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            SavesPayload? payload = await response.Content.ReadFromJsonAsync<SavesPayload>();
            payload.ShouldNotBeNull();
            payload.Saves.Count.ShouldBe(2);

            SavePayload damaged = payload.Saves.Single(row => row.SaveId == damagedId);
            damaged.Unreadable.ShouldBe(true);
            damaged.UnreadableReason.ShouldNotBeNullOrWhiteSpace();
            damaged.Name.ShouldBe(damagedFileName);
            damaged.SchemaVersion.ShouldBe(0);
            damaged.CurrentSeason.ShouldBe(0);
            damaged.Phase.ShouldBe(string.Empty);

            SavePayload healthy = payload.Saves.Single(row => row.SaveId == healthyId);
            healthy.Unreadable.ShouldBe(false);
            healthy.UnreadableReason.ShouldBeNull();
            healthy.Name.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-list-damaged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }

    internal sealed record SavesPayload(IReadOnlyList<SavePayload> Saves);

    internal sealed record SavePayload(
        Guid SaveId,
        string? Name,
        int? SchemaVersion,
        int? CurrentSeason,
        string? Phase,
        bool? Unreadable,
        string? UnreadableReason);
}
