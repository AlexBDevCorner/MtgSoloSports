using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Simulation;

/// <summary>
/// MSS-144 HTTP contract proof: corrupted sporting state surfaces through the
/// real pipeline as a 500 server failure carrying the corruption message,
/// never as a client error (400/409). The corrupted history is a forked
/// Season 1 complete template plus the named corruption injections; the
/// advance endpoint still executes its migration, load and validation chain
/// in every case.
/// </summary>
public sealed class CorruptionHttpTests
{
    [Fact]
    public async Task MissingRngRow_AdvanceRound_Returns500WithCorruptionMessage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(SavesRootOf(root));
            SaveStore store = factory.Services.GetRequiredService<SaveStore>();
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                context.RngStates.RemoveRange(context.RngStates.ToList());
                await context.SaveChangesAsync();
            }

            int leagueId = await FirstLeagueIdAsync(client, saveId);
            using HttpResponseMessage response = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/rounds/advance", content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            ErrorPayload? payload = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            payload.ShouldNotBeNull();
            payload.Error.ShouldNotBeNullOrWhiteSpace();
            payload.Error.ShouldContain("RNG state", Case.Insensitive);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CorruptStageCursor_AdvanceRound_Returns500WithCorruptionMessage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(SavesRootOf(root));
            int leagueId = await FirstLeagueIdAsync(client, saveId);
            SaveStore store = factory.Services.GetRequiredService<SaveStore>();
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                StageEntity stage = await context.Stages
                    .Where(e => e.LeagueId == leagueId)
                    .OrderBy(e => e.StageNumber)
                    .FirstAsync();
                stage.CompletedRounds = 45;
                await context.SaveChangesAsync();
            }

            using HttpResponseMessage response = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/rounds/advance", content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            ErrorPayload? payload = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            payload.ShouldNotBeNull();
            payload.Error.ShouldNotBeNullOrWhiteSpace();
            payload.Error.ShouldContain("corrupt completed-round count", Case.Insensitive);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static string SavesRootOf(string root) => Path.Combine(root, "saves");

    private static async Task<int> FirstLeagueIdAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage leagues = await client.GetAsync($"/api/saves/{saveId:D}/seasons/1/leagues").ConfigureAwait(false);
        leagues.StatusCode.ShouldBe(HttpStatusCode.OK);
        LeaguesPayload? payload = await leagues.Content.ReadFromJsonAsync<LeaguesPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload.Leagues[0].LeagueId;
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-corruption-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }

    internal sealed record ErrorPayload(string? Error);

    internal sealed record LeaguesPayload(
        Guid SaveId,
        int SeasonNumber,
        bool HasSuperleague,
        string DrawChecksum,
        int ActiveAthletes,
        int PoolAthletes,
        IReadOnlyList<LeaguePayload> Leagues,
        IReadOnlyList<object> PoolCounts);

    internal sealed record LeaguePayload(
        string LeagueName,
        string SportingColor,
        int LeagueId,
        IReadOnlyList<object> Athletes);
}
