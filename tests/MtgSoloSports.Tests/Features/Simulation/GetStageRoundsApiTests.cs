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

public sealed class GetStageRoundsApiTests
{
    [Fact]
    public async Task GetStageRounds_EmptyStage_ReturnsEmptyList()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Stage Rounds Empty", 101UL, 202UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);

            using HttpResponseMessage response = await client.GetAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/stages/1/rounds");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            StageRoundsPayload? payload = await response.Content.ReadFromJsonAsync<StageRoundsPayload>();
            payload.ShouldNotBeNull();
            payload.StageNumber.ShouldBe(1);
            payload.CompletedRounds.ShouldBe(0);
            payload.RoundsPerStage.ShouldBe(16);
            payload.IsStageComplete.ShouldBeFalse();
            payload.Rounds.ShouldBeEmpty();
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetStageRounds_AfterAdvance_ReturnsPersistedFactsWithCardArt()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Stage Rounds Persisted", 303UL, 404UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);

            string rngBefore = await ReadRngAsync(client, saveId);
            AdvancePayload advance = await AdvanceOneRoundAsync(client, saveId, leagueId);
            await AdvanceOneRoundAsync(client, saveId, leagueId);

            StageRoundsPayload payload = await FetchStageRoundsAsync(client, saveId, leagueId, 1);
            AssertStageShape(payload, saveId, leagueId);
            AssertRoundMatchesAdvance(payload.Rounds[0], advance);
            AssertCardReferences(payload.Rounds[0]);

            StageRoundsPayload again = await FetchStageRoundsAsync(client, saveId, leagueId, 1);
            string.Equals(again.Rounds[0].PayloadChecksum, payload.Rounds[0].PayloadChecksum, StringComparison.Ordinal).ShouldBeTrue();

            string rngAfter = await ReadRngAsync(client, saveId);
            string.Equals(rngAfter, rngBefore, StringComparison.Ordinal).ShouldBeFalse();
            string rngAfterReads = await ReadRngAsync(client, saveId);
            string.Equals(rngAfterReads, rngAfter, StringComparison.Ordinal).ShouldBeTrue();
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetStageRounds_Errors_UnknownSaveNotFound_BadLeagueAndStageBadRequest()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Stage Rounds Errors", 55UL, 66UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);

            using HttpResponseMessage unknownSave = await client.GetAsync(
                $"/api/saves/{Guid.NewGuid():D}/leagues/{leagueId}/stages/1/rounds");
            unknownSave.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage unknownLeague = await client.GetAsync(
                $"/api/saves/{saveId:D}/leagues/999999/stages/1/rounds");
            unknownLeague.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            using HttpResponseMessage badStage = await client.GetAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/stages/99/rounds");
            badStage.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<AdvancePayload> AdvanceOneRoundAsync(HttpClient client, Guid saveId, int leagueId)
    {
        using HttpResponseMessage advanced = await client.PostAsync(
            $"/api/saves/{saveId:D}/leagues/{leagueId}/rounds/advance", null).ConfigureAwait(false);
        advanced.StatusCode.ShouldBe(HttpStatusCode.OK);
        AdvancePayload? payload = await advanced.Content.ReadFromJsonAsync<AdvancePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        payload.Placements.Count.ShouldBe(32);
        return payload;
    }

    private static async Task<StageRoundsPayload> FetchStageRoundsAsync(HttpClient client, Guid saveId, int leagueId, int stageNumber)
    {
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/saves/{saveId:D}/leagues/{leagueId}/stages/{stageNumber}/rounds").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        StageRoundsPayload? payload = await response.Content.ReadFromJsonAsync<StageRoundsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static void AssertStageShape(StageRoundsPayload payload, Guid saveId, int leagueId)
    {
        payload.SaveId.ShouldBe(saveId);
        payload.SeasonNumber.ShouldBe(1);
        payload.LeagueId.ShouldBe(leagueId);
        payload.StageNumber.ShouldBe(1);
        payload.CompletedRounds.ShouldBe(2);
        payload.IsStageComplete.ShouldBeFalse();
        payload.Rounds.Count.ShouldBe(2);
        payload.Rounds[0].RoundNumber.ShouldBe(1);
        payload.Rounds[1].RoundNumber.ShouldBe(2);
        payload.Rounds[0].Placements.Count.ShouldBe(32);
        payload.Rounds[0].Placements.Select(p => p.Position).ShouldBe(Enumerable.Range(1, 32).ToList());
    }

    private static void AssertRoundMatchesAdvance(StageRoundPayload round, AdvancePayload advance)
    {
        string.Equals(round.PayloadChecksum, advance.PayloadChecksum, StringComparison.Ordinal).ShouldBeTrue();
        for (int i = 0; i < 32; i++)
        {
            StagePlacement got = round.Placements[i];
            PlacementPayload want = advance.Placements[i];
            got.AthleteId.ShouldBe(want.AthleteId);
            string.Equals(got.Name, want.Name, StringComparison.Ordinal).ShouldBeTrue();
            got.Position.ShouldBe(want.Position);
            got.BaseThousandths.ShouldBe(want.BaseThousandths);
            got.ActiveBonusThousandths.ShouldBe(want.ActiveBonusThousandths);
            got.FinalThousandths.ShouldBe(want.FinalThousandths);
            got.CumulativeBeforeThousandths.ShouldBe(want.CumulativeBeforeThousandths);
            got.CumulativeAfterThousandths.ShouldBe(want.CumulativeAfterThousandths);
            got.RankBefore.ShouldBe(want.RankBefore);
            got.RankAfter.ShouldBe(want.RankAfter);
            got.RankMovement.ShouldBe(want.RankMovement);
        }
    }

    private static void AssertCardReferences(StageRoundPayload round)
    {
        foreach (StagePlacement placement in round.Placements)
        {
            bool hasArt = placement.ImageUrl is not null;
            bool hasFallback = placement.SetCode is not null || !string.IsNullOrWhiteSpace(placement.TypeLine);
            (hasArt || hasFallback).ShouldBeTrue($"placement {placement.Name} has no card reference");
        }
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-stage-rounds-" + Guid.NewGuid().ToString("N"));
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
        IReadOnlyList<PlacementPayload> Placements);

    private sealed record PlacementPayload(
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
        int RankMovement);

    private sealed record StageRoundsPayload(
        Guid SaveId,
        int SeasonNumber,
        int LeagueId,
        string LeagueName,
        int StageNumber,
        int CompletedRounds,
        int RoundsPerStage,
        bool IsStageComplete,
        IReadOnlyList<StageRoundPayload> Rounds);

    private sealed record StageRoundPayload(
        int RoundNumber,
        int RulesVersion,
        string PayloadChecksum,
        IReadOnlyList<StagePlacement> Placements);

    private sealed record StagePlacement(
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
}
