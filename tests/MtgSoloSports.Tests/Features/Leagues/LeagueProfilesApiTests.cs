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

namespace MtgSoloSports.Tests.Features.Leagues;

public sealed class LeagueProfilesApiTests
{
    [Fact]
    public async Task LeaguesOverview_And_Profile_ViaApi()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client);
            await CompleteSeasonAsync(client, saveId);

            using HttpResponseMessage overview = await client.GetAsync($"/api/saves/{saveId:D}/leagues");
            overview.StatusCode.ShouldBe(HttpStatusCode.OK);
            LeaguesOverviewPayload? overviewPayload = await overview.Content.ReadFromJsonAsync<LeaguesOverviewPayload>();
            overviewPayload.ShouldNotBeNull();
            overviewPayload.Leagues.Count.ShouldBe(24);
            overviewPayload.Leagues.Select(l => l.LeagueKey).Distinct(StringComparer.Ordinal).Count().ShouldBe(24);
            overviewPayload.Leagues.All(l => l.CompletedSeasons == 1).ShouldBeTrue();
            overviewPayload.Leagues.All(l => l.LatestChampion is not null).ShouldBeTrue();

            string key = overviewPayload.Leagues
                .OrderBy(l => l.LeagueKey, StringComparer.Ordinal)
                .First().LeagueKey;
            using HttpResponseMessage profile = await client.GetAsync($"/api/saves/{saveId:D}/leagues/{key}");
            profile.StatusCode.ShouldBe(HttpStatusCode.OK);
            LeagueProfilePayload? profilePayload = await profile.Content.ReadFromJsonAsync<LeagueProfilePayload>();
            profilePayload.ShouldNotBeNull();
            profilePayload.LeagueKey.ShouldBe(key);
            profilePayload.CompletedSeasons.ShouldBe(1);
            profilePayload.PodiumHistory.Count.ShouldBe(1);
            profilePayload.AllTimeLeaderboard.Count.ShouldBe(32);
            // All-time totals equal the single completed final table exactly once.
            long podiumTotal = profilePayload.PodiumHistory[0].Champion!.TotalChampionshipPointsThousandths
                + profilePayload.PodiumHistory[0].RunnerUp!.TotalChampionshipPointsThousandths
                + profilePayload.PodiumHistory[0].Third!.TotalChampionshipPointsThousandths;
            long boardTotal = profilePayload.AllTimeLeaderboard
                .OrderBy(r => r.BestRank)
                .Take(3)
                .Sum(r => r.TotalLeaguePointsThousandths);
            boardTotal.ShouldBe(podiumTotal);
            profilePayload.Records.Count.ShouldBeGreaterThan(0);

            using HttpResponseMessage unknown = await client.GetAsync($"/api/saves/{saveId:D}/leagues/superleague");
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            using HttpResponseMessage missing = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/leagues");
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NumericLeagueStandingsRoute_StillServesCurrentTable()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client);
            await CompleteSeasonAsync(client, saveId);

            using HttpResponseMessage competitions = await client.GetAsync(
                $"/api/saves/{saveId:D}/history/seasons/1/competitions");
            competitions.StatusCode.ShouldBe(HttpStatusCode.OK);
            CompetitionsPayload? competitionsPayload = await competitions.Content.ReadFromJsonAsync<CompetitionsPayload>();
            competitionsPayload.ShouldNotBeNull();
            int leagueId = competitionsPayload.Competitions.First().LeagueId;

            using HttpResponseMessage standings = await client.GetAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/standings/current");
            standings.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedCatalogAsync(HttpClient client)
    {
        string bulkJson = UniverseTestCatalog.BuildBulkJson();
        using StringContent content = new(bulkJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage imported = await client.PostAsync("/api/catalog/import", content).ConfigureAwait(false);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<Guid> CreateSaveAsync(HttpClient client)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/saves",
            new { name = "League Profiles Api", seed = 303UL, stream = 404UL }).ConfigureAwait(false);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        SavePayload? payload = await created.Content.ReadFromJsonAsync<SavePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.SaveId;
    }

    private static async Task CompleteSeasonAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage completed = await client.PostAsync(
            $"/api/saves/{saveId:D}/seasons/complete-season", null).ConfigureAwait(false);
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-league-profiles-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record LeaguesOverviewPayload(
        Guid SaveId,
        IReadOnlyList<LeagueSummaryPayload> Leagues);

    private sealed record LeagueSummaryPayload(
        string LeagueKey,
        string Name,
        int CompletedSeasons,
        LeagueChampionPayload? LatestChampion);

    private sealed record LeagueChampionPayload(
        int AthleteId,
        string AthleteName,
        int SeasonNumber,
        int TotalChampionshipPointsThousandths);

    private sealed record LeagueProfilePayload(
        Guid SaveId,
        string LeagueKey,
        int CompletedSeasons,
        IReadOnlyList<PodiumSeasonPayload> PodiumHistory,
        IReadOnlyList<AllTimeRowPayload> AllTimeLeaderboard,
        IReadOnlyList<RecordPayload> Records);

    private sealed record PodiumSeasonPayload(
        int SeasonNumber,
        PodiumAthletePayload? Champion,
        PodiumAthletePayload? RunnerUp,
        PodiumAthletePayload? Third);

    private sealed record PodiumAthletePayload(
        int AthleteId,
        string AthleteName,
        int TotalChampionshipPointsThousandths);

    private sealed record AllTimeRowPayload(
        int AthleteId,
        long TotalLeaguePointsThousandths,
        int BestRank);

    private sealed record RecordPayload(string RecordKey);

    private sealed record CompetitionsPayload(IReadOnlyList<CompetitionPayload> Competitions);

    private sealed record CompetitionPayload(int LeagueId);
}
