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

namespace MtgSoloSports.Tests.Features.Athletes;

public sealed class AthleteProfileApiTests
{
    [Fact]
    public async Task GetProfile_ReturnsHistoryAndCardMetadata()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Profile Api", 2024UL, 3035UL);
            int leagueId = await FirstLeagueIdAsync(client, saveId);

            using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
            bulk.StatusCode.ShouldBe(HttpStatusCode.OK);

            int athleteId = await FirstStandingAthleteAsync(client, saveId, leagueId);
            ProfilePayload? payload = await GetProfileAsync(client, saveId, athleteId);
            payload.AthleteId.ShouldBe(athleteId);
            payload.Card.Name.ShouldNotBeNullOrWhiteSpace();
            payload.Card.SportingColorName.ShouldNotBeNullOrWhiteSpace();
            payload.Card.TypeLine.ShouldNotBeNullOrWhiteSpace();
            payload.Career.SeasonsActive.ShouldBe(1);
            (payload.Career.StageWins + payload.Career.StageSeconds + payload.Career.StageThirds).ShouldBe(payload.Career.StagePodiums);
            payload.Seasons.Count.ShouldBe(1);
            payload.Seasons[0].WasActive.ShouldBeTrue();
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetProfile_UnknownAthlete_ReturnsNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Profile Missing", 11UL, 22UL);
            using HttpResponseMessage missing = await client.GetAsync($"/api/saves/{saveId:D}/athletes/999999");
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<int> FirstLeagueIdAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage progress = await client.GetAsync($"/api/saves/{saveId:D}/seasons/1/progress").ConfigureAwait(false);
        progress.StatusCode.ShouldBe(HttpStatusCode.OK);
        ProgressPayload? payload = await progress.Content.ReadFromJsonAsync<ProgressPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Leagues.OrderBy(l => l.LeagueId).First().LeagueId;
    }

    private static async Task<int> FirstStandingAthleteAsync(HttpClient client, Guid saveId, int leagueId)
    {
        using HttpResponseMessage standings = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{leagueId}/standings/current").ConfigureAwait(false);
        standings.StatusCode.ShouldBe(HttpStatusCode.OK);
        StandingsPayload? payload = await standings.Content.ReadFromJsonAsync<StandingsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Standings[0].AthleteId;
    }

    private static async Task<ProfilePayload> GetProfileAsync(HttpClient client, Guid saveId, int athleteId)
    {
        using HttpResponseMessage profile = await client.GetAsync($"/api/saves/{saveId:D}/athletes/{athleteId}").ConfigureAwait(false);
        profile.StatusCode.ShouldBe(HttpStatusCode.OK);
        ProfilePayload? payload = await profile.Content.ReadFromJsonAsync<ProfilePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<Guid> CreateSaveAsync(HttpClient client, string name, ulong seed, ulong stream)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync("/api/saves", new { name, seed, stream }).ConfigureAwait(false);
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-athlete-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record ProfilePayload(
        Guid SaveId,
        int AthleteId,
        CardPayload Card,
        CareerPayload Career,
        IReadOnlyList<SeasonPayload> Seasons);

    private sealed record CardPayload(
        string Name,
        int SportingColor,
        string SportingColorName,
        IReadOnlyList<string> CreatureTypes,
        string FrontColors,
        string ManaCost,
        string TypeLine,
        string? ImageUrl,
        string? SetCode,
        bool IsArtifact,
        bool HasDevoid,
        bool HasHybridMana);

    private sealed record CareerPayload(
        int SeasonsActive,
        bool IsActive,
        int? CurrentLeagueId,
        string? CurrentLeagueName,
        int? CurrentLeagueKind,
        int RoundWins,
        int StageWins,
        int StageSeconds,
        int StageThirds,
        int StagePodiums,
        int? BestSeasonFinish,
        int? BestSeasonNumber,
        int LifetimeEarnedBonusThousandths,
        int CurrentEffectiveBonusThousandths,
        int LastSeasonNumber,
        int LastStageNumber);

    private sealed record SeasonPayload(
        int SeasonNumber,
        int SeasonId,
        bool WasActive,
        int? LeagueId,
        string? LeagueName,
        int? LeagueKind,
        int RoundWins,
        int StageWins,
        int StageSeconds,
        int StageThirds,
        int? SeasonRank,
        bool IsChampion,
        int EarnedBonusThousandths,
        int TotalChampionshipPointsThousandths,
        int TotalStageScoreThousandths,
        int TotalBaseScoreThousandths);
}
