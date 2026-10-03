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

public sealed class AthleteSearchApiTests
{
    [Fact]
    public async Task Search_ReturnsPagedResults_AndOptions()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Search Api", 2024UL, 3035UL);

            using HttpResponseMessage search = await client.GetAsync(
                $"/api/saves/{saveId:D}/athletes/search?minNonPool=0&take=50&sort=name&dir=asc");
            search.StatusCode.ShouldBe(HttpStatusCode.OK);
            SearchPayload? payload = await search.Content.ReadFromJsonAsync<SearchPayload>();
            payload.ShouldNotBeNull();
            payload.TotalCount.ShouldBe(2048);
            payload.Results.Count.ShouldBe(50);
            SearchResultPayload first = payload.Results[0];
            first.AthleteId.ShouldBeGreaterThan(0);
            first.Name.ShouldNotBeNullOrWhiteSpace();
            first.NonPoolSeasons.ShouldBeGreaterThanOrEqualTo(0);
            first.HonoursCount.ShouldBeGreaterThanOrEqualTo(0);

            using HttpResponseMessage noMatch = await client.GetAsync(
                $"/api/saves/{saveId:D}/athletes/search?q=no-such-athlete-zzz");
            noMatch.StatusCode.ShouldBe(HttpStatusCode.OK);
            SearchPayload? empty = await noMatch.Content.ReadFromJsonAsync<SearchPayload>();
            empty.ShouldNotBeNull();
            empty.TotalCount.ShouldBe(0);

            using HttpResponseMessage options = await client.GetAsync(
                $"/api/saves/{saveId:D}/athletes/search/options");
            options.StatusCode.ShouldBe(HttpStatusCode.OK);
            OptionsPayload? opts = await options.Content.ReadFromJsonAsync<OptionsPayload>();
            opts.ShouldNotBeNull();
            opts.Colours.Count.ShouldBe(8);
            opts.CurrentLeagues.Count.ShouldBeGreaterThan(0);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Search_RejectsInvalidTake_AndUnknownSave()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Search Api Invalid", 11UL, 22UL);

            using HttpResponseMessage invalid = await client.GetAsync(
                $"/api/saves/{saveId:D}/athletes/search?take=9999");
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            using HttpResponseMessage unknown = await client.GetAsync(
                $"/api/saves/{Guid.NewGuid():D}/athletes/search?take=10");
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-search-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record SearchPayload(
        Guid SaveId,
        int TotalCount,
        int Skip,
        int Take,
        IReadOnlyList<SearchResultPayload> Results);

    private sealed record SearchResultPayload(
        int AthleteId,
        string Name,
        string? ImageUrl,
        string TypeLine,
        int SportingColor,
        string SportingColorName,
        IReadOnlyList<string> CreatureTypes,
        bool IsActive,
        string? CurrentLeagueName,
        int? CurrentLeagueKind,
        int NonPoolSeasons,
        int HonoursCount,
        int TitlesCount);

    private sealed record OptionsPayload(
        Guid SaveId,
        IReadOnlyList<object> Colours,
        IReadOnlyList<object> CreatureTypes,
        IReadOnlyList<object> CurrentLeagues,
        int MaxNonPoolSeasons,
        int MaxHonours,
        int MaxTitles,
        bool HasSuperleague,
        bool HasCups);
}
