using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Saves;

/// <summary>
/// MSS-144 regression proof for the persistence failure class. A live save
/// that is missing its RNG row is corrupted sporting state: creating a
/// checkpoint and opening the save both surface a 500 server failure with
/// the corruption message through the real pipeline, never a 400 client
/// error or a body-less generic 500.
/// </summary>
public sealed class SaveCorruptionHttpTests
{
    [Fact]
    public async Task MissingRngRow_CreateCheckpoint_Returns500WithCorruptionMessage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(SavesRootOf(root));
            SaveStore store = factory.Services.GetRequiredService<SaveStore>();
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                context.RngStates.RemoveRange(context.RngStates.ToList());
                await context.SaveChangesAsync();
            }

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/saves/{saveId:D}/checkpoints", new { reason = "corruption-probe" });
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            ErrorPayload? payload = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            payload.ShouldNotBeNull();
            payload.Error.ShouldNotBeNullOrWhiteSpace();
            payload.Error.ShouldContain("missing required rows", Case.Insensitive);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MissingRngRow_OpenSave_Returns500WithCorruptionMessage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(SavesRootOf(root));
            SaveStore store = factory.Services.GetRequiredService<SaveStore>();
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                context.RngStates.RemoveRange(context.RngStates.ToList());
                await context.SaveChangesAsync();
            }

            using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}");
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            ErrorPayload? payload = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            payload.ShouldNotBeNull();
            payload.Error.ShouldNotBeNullOrWhiteSpace();
            payload.Error.ShouldContain("missing required rows", Case.Insensitive);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CorruptSave_ListSaves_SkipsTheCorruptSaveAndListsTheHealthyOnes()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid corruptId = await SharedSaveTemplates.SeedSeason1CompleteAsync(SavesRootOf(root));
            SaveStore store = factory.Services.GetRequiredService<SaveStore>();
            using (SaveDbContext context = store.OpenDbContext(corruptId))
            {
                context.RngStates.RemoveRange(context.RngStates.ToList());
                await context.SaveChangesAsync();
            }

            SaveStore.CreationRecord healthy = await store.CreateAsync(
                "Healthy", 81UL, 82UL, UniverseTestCatalog.Build());

            // The listing deliberately skips unreadable saves instead of failing.
            using HttpResponseMessage response = await client.GetAsync("/api/saves");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            SavesPayload? payload = await response.Content.ReadFromJsonAsync<SavesPayload>();
            payload.ShouldNotBeNull();
            payload.Saves.ShouldHaveSingleItem();
            payload.Saves[0].SaveId.ShouldBe(healthy.Detail.SaveId);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static string SavesRootOf(string root) => Path.Combine(root, "saves");

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-save-corruption-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }

    internal sealed record ErrorPayload(string? Error);

    internal sealed record SavesPayload(IReadOnlyList<SavePayload> Saves);

    internal sealed record SavePayload(Guid SaveId);
}
