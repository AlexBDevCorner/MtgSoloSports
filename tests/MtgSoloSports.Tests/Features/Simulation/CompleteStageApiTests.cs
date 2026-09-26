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

public sealed class CompleteStageApiTests
{
    [Fact]
    public async Task CompleteStage_ViaApi_PersistsStandingsAndAdvancesCursor()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            Guid saveId = await CreateSaveAsync(client, "Stage Api", 101UL, 202UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);

            using HttpResponseMessage completed = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/stages/complete", null);
            completed.StatusCode.ShouldBe(HttpStatusCode.OK);
            CompletePayload? payload = await completed.Content.ReadFromJsonAsync<CompletePayload>();
            payload.ShouldNotBeNull();
            payload.StageNumber.ShouldBe(1);
            payload.CompletedRounds.ShouldBe(16);
            payload.Standings.Count.ShouldBe(32);
            payload.NextStageNumber.ShouldBe(2);

            // Championship points for the stage winner use the table with no bonus.
            StandingPayload winner = payload.Standings.Single(s => s.StageRank == 1);
            winner.ChampionshipPointsThousandths.ShouldBe(77_000);

            // The next stage is now legal for round advancement.
            using HttpResponseMessage advanced = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/rounds/advance", null);
            advanced.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompleteStage_UnknownSave_ReturnsNotFound_UnknownLeague_ReturnsConflict()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Stage Api Errors", 55UL, 66UL);

            using HttpResponseMessage unknownSave = await client.PostAsync(
                $"/api/saves/{Guid.NewGuid():D}/leagues/1/stages/complete", null);
            unknownSave.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage unknownLeague = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/999999/stages/complete", null);
            unknownLeague.StatusCode.ShouldBe(HttpStatusCode.Conflict);
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

    private static async Task<int> FirstLeagueIdAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage leagues = await client.GetAsync($"/api/saves/{saveId:D}/seasons/1/leagues").ConfigureAwait(false);
        leagues.StatusCode.ShouldBe(HttpStatusCode.OK);
        LeaguesPayload? payload = await leagues.Content.ReadFromJsonAsync<LeaguesPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Leagues[0].LeagueId;
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-stage-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record LeaguesPayload(
        Guid SaveId,
        int SeasonNumber,
        bool HasSuperleague,
        string DrawChecksum,
        int ActiveAthletes,
        int PoolAthletes,
        IReadOnlyList<LeaguePayload> Leagues,
        IReadOnlyList<object> PoolCounts);

    private sealed record LeaguePayload(
        string LeagueName,
        string SportingColor,
        int LeagueId,
        IReadOnlyList<object> Athletes);

    private sealed record CompletePayload(
        Guid SaveId,
        int SeasonNumber,
        int LeagueId,
        string LeagueName,
        int StageNumber,
        int RulesVersion,
        int CompletedRounds,
        string StageChecksum,
        ulong RngBeforeState,
        ulong RngBeforeStream,
        ulong RngAfterState,
        ulong RngAfterStream,
        int? NextStageNumber,
        IReadOnlyList<StandingPayload> Standings);

    private sealed record StandingPayload(
        int AthleteId,
        string Name,
        int StageRank,
        int StageScoreThousandths,
        int BaseScoreThousandths,
        int ChampionshipPointsThousandths,
        int RoundWins,
        int EarnedBonusThousandths);
}
