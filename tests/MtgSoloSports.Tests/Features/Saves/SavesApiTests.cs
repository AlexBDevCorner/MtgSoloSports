using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Saves;

public sealed class SavesApiTests
{
    [Fact]
    public async Task FullSaveLifecycle_ViaApi()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage created = await client.PostAsJsonAsync(
                "/api/saves",
                new { name = "Api Universe", seed = 101UL, stream = 202UL });
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
            SavePayload? createdPayload = await created.Content.ReadFromJsonAsync<SavePayload>();
            createdPayload.ShouldNotBeNull();
            createdPayload.Name.ShouldBe("Api Universe");
            createdPayload.SchemaVersion.ShouldBe(SaveSchemaVersion.Current);
            createdPayload.CurrentSeason.ShouldBe(1);
            createdPayload.Phase.ShouldBe("SeasonInProgress");
            createdPayload.RulesVersion.ShouldBe(1);
            created.Headers.Location.ShouldNotBeNull();

            using HttpResponseMessage opened = await client.GetAsync($"/api/saves/{createdPayload.SaveId:D}");
            opened.StatusCode.ShouldBe(HttpStatusCode.OK);
            SavePayload? openedPayload = await opened.Content.ReadFromJsonAsync<SavePayload>();
            openedPayload.ShouldNotBeNull();
            openedPayload.SaveId.ShouldBe(createdPayload.SaveId);
            openedPayload.RngState.ShouldBe(createdPayload.RngState);

            using HttpResponseMessage listed = await client.GetAsync("/api/saves");
            listed.StatusCode.ShouldBe(HttpStatusCode.OK);
            ListPayload? listPayload = await listed.Content.ReadFromJsonAsync<ListPayload>();
            listPayload.ShouldNotBeNull();
            listPayload.Saves.Count.ShouldBe(1);
            listPayload.Saves[0].SaveId.ShouldBe(createdPayload.SaveId);

            using HttpResponseMessage deleted = await client.DeleteAsync($"/api/saves/{createdPayload.SaveId:D}");
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            using HttpResponseMessage reopened = await client.GetAsync($"/api/saves/{createdPayload.SaveId:D}");
            reopened.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            Directory.GetFiles(root, "*.db").Length.ShouldBe(0);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateSave_InvalidName_ReturnsBadRequest()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/saves",
                new { name = "   " });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownSave_ReturnsNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid missing = Guid.NewGuid();
            using HttpResponseMessage opened = await client.GetAsync($"/api/saves/{missing:D}");
            opened.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            using HttpResponseMessage deleted = await client.DeleteAsync($"/api/saves/{missing:D}");
            deleted.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TwoApiSaves_UseSeparateFiles()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage first = await client.PostAsJsonAsync("/api/saves", new { name = "One" });
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
            using HttpResponseMessage second = await client.PostAsJsonAsync("/api/saves", new { name = "Two" });
            second.StatusCode.ShouldBe(HttpStatusCode.Created);

            Directory.GetFiles(root, "*.db").Length.ShouldBe(2);

            using HttpResponseMessage listed = await client.GetAsync("/api/saves");
            ListPayload? listPayload = await listed.Content.ReadFromJsonAsync<ListPayload>();
            listPayload.ShouldNotBeNull();
            listPayload.Saves.Count.ShouldBe(2);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.Configure<SaveStorageOptions>(o => o.SavesRoot = root)));
        return (factory, root);
    }

    private sealed record SavePayload(
        Guid SaveId,
        string Name,
        DateTimeOffset CreatedUtc,
        int SchemaVersion,
        int CurrentSeason,
        string Phase,
        string RngAlgorithm,
        int RngVersion,
        string RngState,
        string RngStream,
        int RulesVersion);

    private sealed record ListPayload(IReadOnlyList<SavePayload> Saves);
}
