using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Simulation;

public sealed class FastSimulationApiTests
{
    [Fact]
    public async Task CompleteSeason_ViaApi_CompletesRemainingStagesWithProgress()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Fast Season Api", 101UL, 202UL);

            using HttpResponseMessage response = await client.PostAsync(
                $"/api/saves/{saveId:D}/seasons/complete-season", null);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            CompleteSeasonPayload? payload = await response.Content.ReadFromJsonAsync<CompleteSeasonPayload>();
            payload.ShouldNotBeNull();
            payload.SeasonNumber.ShouldBe(1);
            payload.StagesCompleted.ShouldBe(32);
            payload.GlobalStageBefore.ShouldBe(1);
            payload.GlobalStageAfter.ShouldBe(33);
            payload.IsSeasonComplete.ShouldBeTrue();
            payload.Progress.ShouldNotBeNull();
            payload.Progress.StagesCompleted.ShouldBe(32);
            payload.Progress.TotalStagesInSeason.ShouldBe(32);

            // Second call conflicts: the season is already complete.
            using HttpResponseMessage again = await client.PostAsync(
                $"/api/saves/{saveId:D}/seasons/complete-season", null);
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteSeason_UnknownSave_ReturnsNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage unknown = await client.PostAsync(
                $"/api/saves/{Guid.NewGuid():D}/seasons/complete-season", null);
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SimulateSeasons_ViaApi_CompletesOneSeasonWithProgress()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Fast Simulate Api", 303UL, 404UL);

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/saves/{saveId:D}/simulate-seasons", new { seasons = 1 });
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            SimulatePayload? payload = await response.Content.ReadFromJsonAsync<SimulatePayload>();
            payload.ShouldNotBeNull();
            payload.SeasonsRequested.ShouldBe(1);
            payload.SeasonsCompleted.ShouldBe(1);
            payload.StagesCompleted.ShouldBe(32);
            payload.StartSeasonNumber.ShouldBe(1);
            payload.EndSeasonNumber.ShouldBe(2);
            payload.Progress.ShouldNotBeNull();
            payload.Progress.SeasonsRequested.ShouldBe(1);
            payload.Progress.SeasonsCompleted.ShouldBe(1);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(501)]
    public async Task SimulateSeasons_OutOfRange_ReturnsBadRequest(int seasons)
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Fast Bounds Api", 505UL, 606UL);

            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/api/saves/{saveId:D}/simulate-seasons", new { seasons });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SimulateSeasons_UnknownSave_ReturnsNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage unknown = await client.PostAsJsonAsync(
                $"/api/saves/{Guid.NewGuid():D}/simulate-seasons", new { seasons = 1 });
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<Guid> CreateSaveAsync(HttpClient client, string name, ulong seed, ulong stream)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/saves",
            new { name, seed, stream }).ConfigureAwait(false);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        SavePayload? payload = await created.Content.ReadFromJsonAsync<SavePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.SaveId;
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-fast-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }

    private sealed record SavePayload(Guid SaveId);

    private sealed record CompleteSeasonPayload(
        Guid SaveId,
        int SeasonNumber,
        int StagesCompleted,
        int GlobalStageBefore,
        int GlobalStageAfter,
        bool IsSeasonComplete,
        ulong RngBeforeState,
        ulong RngBeforeStream,
        ulong RngAfterState,
        ulong RngAfterStream,
        CompleteSeasonProgressPayload Progress);

    private sealed record CompleteSeasonProgressPayload(
        int StagesCompleted,
        int TotalStagesInSeason,
        int GlobalStageBefore,
        int GlobalStageAfter);

    private sealed record SimulatePayload(
        Guid SaveId,
        int SeasonsRequested,
        int SeasonsCompleted,
        int StagesCompleted,
        int PostseasonStepsCompleted,
        int StartSeasonNumber,
        int EndSeasonNumber,
        int GlobalStage,
        string ComputedPhase,
        bool IsCurrentSeasonComplete,
        ulong RngBeforeState,
        ulong RngBeforeStream,
        ulong RngAfterState,
        ulong RngAfterStream,
        SimulateProgressPayload Progress);

    private sealed record SimulateProgressPayload(
        int SeasonsRequested,
        int SeasonsCompleted,
        int StagesCompleted,
        int PostseasonStepsCompleted,
        int StartSeasonNumber,
        int EndSeasonNumber);
}
