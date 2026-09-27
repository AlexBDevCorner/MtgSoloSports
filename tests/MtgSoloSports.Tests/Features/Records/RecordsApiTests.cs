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

namespace MtgSoloSports.Tests.Features.Records;

public sealed class RecordsApiTests
{
    [Fact]
    public async Task RecordsEndpoints_AfterOneSeason_ReturnHonoursRecordsAndHallOfFame()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Records API", 424201UL, 848402UL);
            await SimulateSeasonsAsync(client, saveId, 1);

            HonoursPayload honours = await FetchHonoursAsync(client, saveId);
            honours.Honours.Count.ShouldBe(8);

            RecordsPayload records = await FetchRecordsAsync(client, saveId);
            records.Records.Count.ShouldBe(13);
            RecordPayload feeder = records.Records.Single(r => string.Equals(r.RecordKey, "feeder_titles", StringComparison.Ordinal));
            feeder.Value.ShouldBe(1);
            feeder.Holders.Count.ShouldBe(8);
            feeder.IsVacant.ShouldBeFalse();
            records.Records.Single(r => string.Equals(r.RecordKey, "superleague_titles", StringComparison.Ordinal)).IsVacant.ShouldBeTrue();
            records.RecentHistory.Count.ShouldBeGreaterThan(0);

            HallOfFamePayload fame = await FetchHallOfFameAsync(client, saveId);
            fame.Leaders.Count.ShouldBeGreaterThan(0);
            fame.Leaders[0].Rank.ShouldBe(1);
            fame.Leaders[0].TotalTitles.ShouldBe(1);

            int anyAthlete = honours.Honours[0].AthleteId;
            HonoursPayload filtered = await FetchHonoursByAthleteAsync(client, saveId, anyAthlete);
            filtered.Honours.Count.ShouldBeGreaterThanOrEqualTo(1);
            filtered.Honours.All(h => h.AthleteId == anyAthlete).ShouldBeTrue();
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordsApi_Errors_UnknownSaveNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Records Errors", 111003UL, 222004UL);

            using HttpResponseMessage unknownRecords = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/records");
            unknownRecords.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage unknownHonours = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/honours");
            unknownHonours.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage unknownFame = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/hall-of-fame");
            unknownFame.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage badAthlete = await client.GetAsync($"/api/saves/{saveId:D}/honours?athleteId=0");
            badAthlete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SimulateSeasonsAsync(HttpClient client, Guid saveId, int seasons)
    {
        using HttpResponseMessage simulated = await client.PostAsJsonAsync(
            $"/api/saves/{saveId:D}/simulate-seasons", new { seasons }).ConfigureAwait(false);
        simulated.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<HonoursPayload> FetchHonoursAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/honours").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HonoursPayload? payload = await response.Content.ReadFromJsonAsync<HonoursPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<HonoursPayload> FetchHonoursByAthleteAsync(HttpClient client, Guid saveId, int athleteId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/honours?athleteId={athleteId}").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HonoursPayload? payload = await response.Content.ReadFromJsonAsync<HonoursPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<RecordsPayload> FetchRecordsAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/records").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        RecordsPayload? payload = await response.Content.ReadFromJsonAsync<RecordsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<HallOfFamePayload> FetchHallOfFameAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/hall-of-fame?take=20").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HallOfFamePayload? payload = await response.Content.ReadFromJsonAsync<HallOfFamePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-records-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record HonoursPayload(
        Guid SaveId,
        IReadOnlyList<HonourPayload> Honours);

    private sealed record HonourPayload(
        int SeasonNumber,
        int SeasonId,
        int LeagueId,
        string LeagueName,
        int LeagueKind,
        string HonourKind,
        int AthleteId,
        string AthleteName);

    private sealed record RecordsPayload(
        Guid SaveId,
        IReadOnlyList<RecordPayload> Records,
        IReadOnlyList<RecordHistoryPayload> RecentHistory);

    private sealed record RecordPayload(
        string RecordKey,
        string Label,
        int Value,
        string ValueDisplay,
        bool IsBonus,
        bool IsVacant,
        IReadOnlyList<RecordHolderPayload> Holders);

    private sealed record RecordHolderPayload(int AthleteId, string AthleteName);

    private sealed record RecordHistoryPayload(
        int AthleteId,
        string AthleteName,
        string RecordKey,
        int Value,
        int PriorValue,
        int SeasonNumber,
        string Text);

    private sealed record HallOfFamePayload(
        Guid SaveId,
        int Take,
        int TotalAthletes,
        IReadOnlyList<HallOfFameLeaderPayload> Leaders);

    private sealed record HallOfFameLeaderPayload(
        int Rank,
        int AthleteId,
        string AthleteName,
        int SportingColor,
        string SportingColorName,
        int FeederTitles,
        int SuperleagueTitles,
        int TotalTitles,
        int StageWins,
        int RoundWins,
        int SuperleagueAppearances,
        int TotalAppearances,
        int LongestSuperleagueTenure,
        int Promotions,
        int Relegations,
        int CurrentEffectiveBonusThousandths,
        int LongestTitleStreak,
        int LongestStageWinStreak);
}
