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

public sealed class SeasonCompletionApiTests
{
    [Fact]
    public async Task BulkCompleteAll_ViaApi_CompletesGlobalStageForEveryLeague()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Bulk Api", 101UL, 202UL);

            using HttpResponseMessage bulk = await client.PostAsync(
                $"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            BulkPayload? bulkPayload = await bulk.Content.ReadFromJsonAsync<BulkPayload>();
            bulkPayload.ShouldNotBeNull();
            bulkPayload.CompletedStage.ShouldBe(1);
            bulkPayload.GlobalStageAfter.ShouldBe(2);
            bulkPayload.IsSeasonComplete.ShouldBeFalse();
            bulkPayload.Leagues.Count.ShouldBe(8);

            // Progress tracks the new global stage.
            using HttpResponseMessage progress = await client.GetAsync(
                $"/api/saves/{saveId:D}/seasons/1/progress");
            progress.StatusCode.ShouldBe(HttpStatusCode.OK);
            ProgressPayload? progressPayload = await progress.Content.ReadFromJsonAsync<ProgressPayload>();
            progressPayload.ShouldNotBeNull();
            progressPayload.GlobalStage.ShouldBe(2);
            progressPayload.IsSeasonComplete.ShouldBeFalse();
            progressPayload.Leagues.Count.ShouldBe(8);

            // Current standings for the first league accumulate Stage 1.
            int firstLeague = bulkPayload.Leagues[0].LeagueId;
            using HttpResponseMessage standings = await client.GetAsync(
                $"/api/saves/{saveId:D}/leagues/{firstLeague}/standings/current");
            standings.StatusCode.ShouldBe(HttpStatusCode.OK);
            StandingsPayload? standingsPayload = await standings.Content.ReadFromJsonAsync<StandingsPayload>();
            standingsPayload.ShouldNotBeNull();
            standingsPayload.CompletedStages.ShouldBe(1);
            standingsPayload.IsFinal.ShouldBeFalse();
            standingsPayload.Standings.Count.ShouldBe(32);

            // Completed season table does not exist yet.
            using HttpResponseMessage table = await client.GetAsync(
                $"/api/saves/{saveId:D}/leagues/{firstLeague}/seasons/1/table");
            table.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BulkCompleteAll_UnknownSave_ReturnsNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage unknown = await client.PostAsync(
                $"/api/saves/{Guid.NewGuid():D}/stages/complete-all", null);
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-season-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record BulkPayload(
        Guid SaveId,
        int SeasonNumber,
        int CompletedStage,
        int GlobalStageAfter,
        bool IsSeasonComplete,
        IReadOnlyList<BulkLeaguePayload> Leagues);

    private sealed record BulkLeaguePayload(
        int LeagueId,
        string LeagueName,
        int StageNumber,
        string StageChecksum,
        int? NextStageNumber);

    private sealed record ProgressPayload(
        Guid SaveId,
        int SeasonNumber,
        int GlobalStage,
        bool IsSeasonComplete,
        IReadOnlyList<ProgressLeaguePayload> Leagues);

    private sealed record ProgressLeaguePayload(
        int LeagueId,
        string LeagueName,
        int? CurrentStage,
        int CompletedStages,
        bool IsLeagueComplete);

    private sealed record StandingsPayload(
        Guid SaveId,
        int SeasonNumber,
        int LeagueId,
        string LeagueName,
        int CompletedStages,
        int GlobalStage,
        bool IsSeasonComplete,
        bool IsFinal,
        string SeasonChecksum,
        IReadOnlyList<StandingsEntryPayload> Standings);

    private sealed record StandingsEntryPayload(
        int AthleteId,
        string Name,
        int SeasonRank,
        int TotalChampionshipPointsThousandths,
        int TotalStageScoreThousandths,
        int TotalBaseScoreThousandths,
        int StageWins,
        int RoundWins,
        bool IsChampion);
}
