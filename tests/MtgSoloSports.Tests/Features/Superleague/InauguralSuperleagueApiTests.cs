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

namespace MtgSoloSports.Tests.Features.Superleague;

public sealed class InauguralSuperleagueApiTests
{
    [Fact]
    public async Task Inaugural_BeforeSeasonComplete_PostConflicts_GetNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Inaugural Gates", 11UL, 22UL);

            using HttpResponseMessage post = await client.PostAsync($"/api/saves/{saveId:D}/superleague/inaugural", null);
            post.StatusCode.ShouldBe(HttpStatusCode.Conflict);

            using HttpResponseMessage get = await client.GetAsync($"/api/saves/{saveId:D}/superleague/inaugural");
            get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Inaugural_AfterSeasonOne_Creates32AndExposesRoster()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Inaugural Api", 707UL, 808UL);

            for (int stage = 1; stage <= 32; stage++)
            {
                using HttpResponseMessage bulk = await client.PostAsync($"/api/saves/{saveId:D}/stages/complete-all", null);
                bulk.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            using HttpResponseMessage post = await client.PostAsync($"/api/saves/{saveId:D}/superleague/inaugural", null);
            post.StatusCode.ShouldBe(HttpStatusCode.OK);
            CreatePayload? created = await post.Content.ReadFromJsonAsync<CreatePayload>();
            created.ShouldNotBeNull();
            created.Members.Count.ShouldBe(32);
            created.MovementCount.ShouldBe(32);
            created.FeederRetention.Count.ShouldBe(24);
            created.FeederRetention.Count(r => r.RetainedCount == 28).ShouldBe(8);
            created.FeederRetention.Count(r => r.RetainedCount == 32).ShouldBe(16);

            using HttpResponseMessage get = await client.GetAsync($"/api/saves/{saveId:D}/superleague/inaugural");
            get.StatusCode.ShouldBe(HttpStatusCode.OK);
            RosterPayload? roster = await get.Content.ReadFromJsonAsync<RosterPayload>();
            roster.ShouldNotBeNull();
            roster.Members.Count.ShouldBe(32);
            roster.MovementCount.ShouldBe(32);
            roster.Members.Select(m => m.AthleteId).ShouldBe(created.Members.Select(m => m.AthleteId).ToList());

            using HttpResponseMessage again = await client.PostAsync($"/api/saves/{saveId:D}/superleague/inaugural", null);
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Inaugural_UnknownSave_ReturnsNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);

            using HttpResponseMessage post = await client.PostAsync($"/api/saves/{Guid.NewGuid():D}/superleague/inaugural", null);
            post.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage get = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/superleague/inaugural");
            get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-inaugural-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record CreatePayload(
        Guid SaveId,
        int SeasonOneNumber,
        int SeasonTwoNumber,
        int SuperleagueLeagueId,
        string SuperleagueLeagueName,
        IReadOnlyList<MemberPayload> Members,
        IReadOnlyList<RetentionPayload> FeederRetention,
        int PoolCount,
        int MovementCount);

    private sealed record RosterPayload(
        Guid SaveId,
        int SeasonNumber,
        int SuperleagueLeagueId,
        string SuperleagueLeagueName,
        IReadOnlyList<MemberPayload> Members,
        int MovementCount);

    private sealed record MemberPayload(
        int AthleteId,
        string Name,
        string SportingColor,
        int FromLeagueId,
        string FromLeagueName,
        int FromSeasonRank);

    private sealed record RetentionPayload(
        int LeagueId,
        string LeagueName,
        string SportingColor,
        int RetainedCount);
}
