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

namespace MtgSoloSports.Tests.Features.History;

public sealed class HistoryApiTests
{
    [Fact]
    public async Task HistoryNavigation_SeasonToRound_ReplaysIdenticalResultAfterBulkSimulation()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "History API", 909UL, 1010UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);

            HistoryReplayPayload before = await CaptureFirstRoundAsync(client, saveId, leagueId);
            await AssertReplayIsStableAsync(client, saveId, leagueId, before);
            await SimulateOneSeasonAsync(client, saveId);
            await AssertReplayAfterBulkAsync(client, saveId, leagueId, before);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HistoryApi_Errors_UnknownSaveNotFound_BadIdsBadRequest()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "History Errors", 1113UL, 1414UL);
            await AssertHistoryErrorsAsync(client, saveId);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<HistoryReplayPayload> CaptureFirstRoundAsync(HttpClient client, Guid saveId, int leagueId)
    {
        AdvancePayload advance = await AdvanceOneRoundAsync(client, saveId, leagueId).ConfigureAwait(false);
        HistoryReplayPayload before = await FetchReplayAsync(client, saveId, 1, leagueId, 1, 1).ConfigureAwait(false);
        before.PayloadChecksum.ShouldBe(advance.PayloadChecksum);
        before.Placements.Count.ShouldBe(32);
        return before;
    }

    private static async Task AssertReplayIsStableAsync(HttpClient client, Guid saveId, int leagueId, HistoryReplayPayload before)
    {
        string rngBeforeReplay = await ReadRngAsync(client, saveId).ConfigureAwait(false);
        HistoryReplayPayload reread = await FetchReplayAsync(client, saveId, 1, leagueId, 1, 1).ConfigureAwait(false);
        reread.PayloadChecksum.ShouldBe(before.PayloadChecksum);
        string rngAfterReplay = await ReadRngAsync(client, saveId).ConfigureAwait(false);
        rngAfterReplay.ShouldBe(rngBeforeReplay);

        HistorySeasonsPayload seasonsBefore = await FetchSeasonsAsync(client, saveId).ConfigureAwait(false);
        seasonsBefore.Seasons.Count.ShouldBe(1);

        HistoryRoundsPayload summaries = await FetchRoundSummariesAsync(client, saveId, 1, leagueId, 1).ConfigureAwait(false);
        summaries.Rounds.Count.ShouldBe(1);
        summaries.Rounds[0].PayloadChecksum.ShouldBe(before.PayloadChecksum);
    }

    private static async Task AssertReplayAfterBulkAsync(HttpClient client, Guid saveId, int leagueId, HistoryReplayPayload before)
    {
        HistoryReplayPayload after = await FetchReplayAsync(client, saveId, 1, leagueId, 1, 1).ConfigureAwait(false);
        AssertReplayEqual(after, before);

        string rngBeforeFinal = await ReadRngAsync(client, saveId).ConfigureAwait(false);
        await FetchReplayAsync(client, saveId, 1, leagueId, 1, 1).ConfigureAwait(false);
        string rngAfterFinal = await ReadRngAsync(client, saveId).ConfigureAwait(false);
        rngAfterFinal.ShouldBe(rngBeforeFinal);

        HistorySeasonsPayload seasonsAfter = await FetchSeasonsAsync(client, saveId).ConfigureAwait(false);
        seasonsAfter.Seasons.Count.ShouldBeGreaterThanOrEqualTo(2);

        await AssertMissingHistoryIsNotFoundAsync(client, saveId, leagueId).ConfigureAwait(false);
    }

    private static void AssertReplayEqual(HistoryReplayPayload after, HistoryReplayPayload before)
    {
        after.PayloadChecksum.ShouldBe(before.PayloadChecksum);
        after.Placements.Count.ShouldBe(before.Placements.Count);
        for (int i = 0; i < before.Placements.Count; i++)
        {
            after.Placements[i].AthleteId.ShouldBe(before.Placements[i].AthleteId);
            after.Placements[i].Name.ShouldBe(before.Placements[i].Name);
            after.Placements[i].Position.ShouldBe(before.Placements[i].Position);
            after.Placements[i].FinalThousandths.ShouldBe(before.Placements[i].FinalThousandths);
            after.Placements[i].CumulativeAfterThousandths.ShouldBe(before.Placements[i].CumulativeAfterThousandths);
        }
    }

    private static async Task SimulateOneSeasonAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage simulated = await client.PostAsJsonAsync(
            $"/api/saves/{saveId:D}/simulate-seasons", new { seasons = 1 }).ConfigureAwait(false);
        simulated.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task AssertMissingHistoryIsNotFoundAsync(HttpClient client, Guid saveId, int leagueId)
    {
        using HttpResponseMessage outOfRange = await client.GetAsync(
            $"/api/saves/{saveId:D}/history/seasons/1/competitions/{leagueId}/stages/1/rounds/99").ConfigureAwait(false);
        outOfRange.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using HttpResponseMessage unknownSeason = await client.GetAsync(
            $"/api/saves/{saveId:D}/history/seasons/99/competitions").ConfigureAwait(false);
        unknownSeason.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task AssertHistoryErrorsAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage unknownSave = await client.GetAsync(
            $"/api/saves/{Guid.NewGuid():D}/history/seasons").ConfigureAwait(false);
        unknownSave.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        using HttpResponseMessage badSeason = await client.GetAsync(
            $"/api/saves/{saveId:D}/history/seasons/0/competitions").ConfigureAwait(false);
        badSeason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using HttpResponseMessage badLeague = await client.GetAsync(
            $"/api/saves/{saveId:D}/history/seasons/1/competitions/0/stages").ConfigureAwait(false);
        badLeague.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<AdvancePayload> AdvanceOneRoundAsync(HttpClient client, Guid saveId, int leagueId)
    {
        using HttpResponseMessage advanced = await client.PostAsync(
            $"/api/saves/{saveId:D}/leagues/{leagueId}/rounds/advance", null).ConfigureAwait(false);
        advanced.StatusCode.ShouldBe(HttpStatusCode.OK);
        AdvancePayload? payload = await advanced.Content.ReadFromJsonAsync<AdvancePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<HistoryReplayPayload> FetchReplayAsync(HttpClient client, Guid saveId, int season, int leagueId, int stage, int round)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/saves/{saveId:D}/history/seasons/{season}/competitions/{leagueId}/stages/{stage}/rounds/{round}").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HistoryReplayPayload? payload = await response.Content.ReadFromJsonAsync<HistoryReplayPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<HistoryRoundsPayload> FetchRoundSummariesAsync(HttpClient client, Guid saveId, int season, int leagueId, int stage)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/saves/{saveId:D}/history/seasons/{season}/competitions/{leagueId}/stages/{stage}/rounds").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HistoryRoundsPayload? payload = await response.Content.ReadFromJsonAsync<HistoryRoundsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<HistorySeasonsPayload> FetchSeasonsAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/saves/{saveId:D}/history/seasons").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HistorySeasonsPayload? payload = await response.Content.ReadFromJsonAsync<HistorySeasonsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<string> ReadRngAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage detail = await client.GetAsync($"/api/saves/{saveId:D}").ConfigureAwait(false);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        SaveDetailPayload? payload = await detail.Content.ReadFromJsonAsync<SaveDetailPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.RngState + ":" + payload.RngStream;
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-history-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record SaveDetailPayload(string RngState, string RngStream);

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

    private sealed record AdvancePayload(
        Guid SaveId,
        int SeasonNumber,
        int LeagueId,
        string LeagueName,
        int StageNumber,
        int RoundNumber,
        int RulesVersion,
        string PayloadChecksum,
        ulong RngBeforeState,
        ulong RngBeforeStream,
        ulong RngAfterState,
        ulong RngAfterStream,
        IReadOnlyList<ReplayPlacement> Placements);

    private sealed record HistoryReplayPayload(
        Guid SaveId,
        int SeasonNumber,
        int LeagueId,
        string LeagueName,
        int StageNumber,
        int RoundNumber,
        int RulesVersion,
        string PayloadChecksum,
        ulong RngBeforeState,
        ulong RngBeforeStream,
        ulong RngAfterState,
        ulong RngAfterStream,
        IReadOnlyList<ReplayPlacement> Placements);

    private sealed record ReplayPlacement(
        int AthleteId,
        string Name,
        int Position,
        int BaseThousandths,
        int ActiveBonusThousandths,
        int FinalThousandths,
        int CumulativeBeforeThousandths,
        int CumulativeAfterThousandths,
        int RankBefore,
        int RankAfter,
        int RankMovement,
        string? ImageUrl,
        string? SetCode,
        string TypeLine);

    private sealed record HistorySeasonsPayload(
        Guid SaveId,
        IReadOnlyList<HistorySeason> Seasons);

    private sealed record HistorySeason(
        int SeasonNumber,
        bool HasSuperleague,
        bool IsComplete,
        int CompetitionCount,
        int CompletedStages,
        int TotalRounds);

    private sealed record HistoryRoundsPayload(
        Guid SaveId,
        int SeasonNumber,
        int LeagueId,
        string LeagueName,
        int StageNumber,
        int CompletedRounds,
        int RoundsPerStage,
        bool IsStageComplete,
        IReadOnlyList<HistoryRoundSummary> Rounds);

    private sealed record HistoryRoundSummary(
        int RoundNumber,
        int RulesVersion,
        string PayloadChecksum);
}
