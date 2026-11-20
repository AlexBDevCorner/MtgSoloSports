using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Leagues;

/// <summary>
/// MSS-144 regression proof for the provisional current-standings failure
/// class. A save whose persisted stage standings hold championship points
/// that no longer match the rules scoring table is server-side corruption:
/// accumulating those rows for the live provisional table must surface a 500
/// server failure carrying the corruption message through the real
/// pipeline, never a 400 client error. The trigger state is one real
/// production stage completion plus one tampered standing row, so the named
/// action, the read chain and the corruption assertion execute in every run.
/// </summary>
public sealed class CurrentStandingsCorruptionHttpTests
{
    [Fact]
    public async Task CorruptPersistedStageStandings_GetCurrentStandings_Returns500WithCorruptionMessage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            (Guid saveId, int leagueId) = await CompleteOneStageThenCorruptAsync(factory);

            using HttpResponseMessage response = await client.GetAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/standings/current");
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            ErrorPayload? payload = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            payload.ShouldNotBeNull();
            payload.Error.ShouldNotBeNullOrWhiteSpace();
            payload.Error.ShouldContain("corrupt stage standings", Case.Insensitive);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    /// <summary>
    /// Builds the corrupted trigger state through production handlers: a
    /// fresh save, one genuine stage completion in the lowest-id league,
    /// then a tampered persisted stage-1 rank-1 standing whose championship
    /// points no longer match the snapshot rules scoring table.
    /// </summary>
    private static async Task<(Guid SaveId, int LeagueId)> CompleteOneStageThenCorruptAsync(
        WebApplicationFactory<Program> factory)
    {
        SaveStore store = factory.Services.GetRequiredService<SaveStore>();
        SaveStore.CreationRecord created = await store.CreateAsync(
                "Corrupt Standings", 131UL, 313UL, UniverseTestCatalog.Build())
            .ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        int leagueId = await FirstLeagueIdAsync(store, saveId).ConfigureAwait(false);

        await new CompleteStageHandler(store).HandleAsync(saveId, leagueId).ConfigureAwait(false);

        using SaveDbContext context = store.OpenDbContext(saveId);
        StageStandingEntity row = await context.StageStandings
            .SingleAsync(e => e.LeagueId == leagueId && e.StageNumber == 1 && e.StageRank == 1)
            .ConfigureAwait(false);
        row.ChampionshipPointsThousandths += 1;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return (saveId, leagueId);
    }

    private static async Task<int> FirstLeagueIdAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        LeagueEntity league = await context.Leagues
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .FirstAsync()
            .ConfigureAwait(false);
        return league.Id;
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-current-standings-corruption-http-" + Guid.NewGuid().ToString("N"));
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
}
