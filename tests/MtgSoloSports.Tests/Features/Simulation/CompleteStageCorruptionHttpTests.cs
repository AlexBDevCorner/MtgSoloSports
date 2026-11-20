using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Simulation;

/// <summary>
/// MSS-144 regression proof for the complete-stage failure class. A partially
/// played stage whose persisted round payload holds corrupt placements is
/// server-side corruption: completing the stage — single league or bulk —
/// must surface a 500 server failure carrying the corruption message through
/// the real pipeline, never a 400 client error, and the invariant failure must
/// abort the mutation without persisting a completion.
/// </summary>
public sealed class CompleteStageCorruptionHttpTests
{
    [Fact]
    public async Task CorruptPersistedPlacements_CompleteStage_Returns500WithCorruptionMessage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            (Guid saveId, int leagueId) = await PlayPartialStageAsync(factory);

            using HttpResponseMessage response = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/stages/complete", content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            ErrorPayload? payload = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            payload.ShouldNotBeNull();
            payload.Error.ShouldNotBeNullOrWhiteSpace();
            payload.Error.ShouldContain("corrupt round placements", Case.Insensitive);

            // The invariant failure aborted the mutation: the stage was not finalized.
            using (SaveDbContext context = factory.Services.GetRequiredService<SaveStore>().OpenDbContext(saveId))
            {
                StageEntity stage = await context.Stages
                    .AsNoTracking()
                    .SingleAsync(e => e.LeagueId == leagueId && e.StageNumber == 1);
                stage.IsComplete.ShouldBeFalse();
                (await context.Rounds.CountAsync(e => e.LeagueId == leagueId && e.StageNumber == 1))
                    .ShouldBe(PartialRounds);
            }
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CorruptPersistedPlacements_CompleteAllLeagues_Returns500WithCorruptionMessage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            (Guid saveId, int _) = await PlayPartialStageAsync(factory);

            using HttpResponseMessage response = await client.PostAsync(
                $"/api/saves/{saveId:D}/stages/complete-all", content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            ErrorPayload? payload = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            payload.ShouldNotBeNull();
            payload.Error.ShouldNotBeNullOrWhiteSpace();
            payload.Error.ShouldContain("corrupt round placements", Case.Insensitive);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private const int PartialRounds = 3;

    /// <summary>
    /// Builds the corrupted trigger state through production handlers: a fresh
    /// save, three real advance-round commits in the lowest-id league, then a
    /// tampered persisted round-2 payload whose placements no longer match the
    /// snapshot rules. The bulk endpoint's ascending league order starts with
    /// this league, so the corruption is hit before any league commits.
    /// </summary>
    private static async Task<(Guid SaveId, int LeagueId)> PlayPartialStageAsync(WebApplicationFactory<Program> factory)
    {
        SaveStore store = factory.Services.GetRequiredService<SaveStore>();
        SaveStore.CreationRecord created = await store.CreateAsync(
                "Corrupt Stage", 121UL, 212UL, UniverseTestCatalog.Build())
            .ConfigureAwait(false);
        Guid saveId = created.Detail.SaveId;
        int leagueId = await FirstLeagueIdAsync(store, saveId).ConfigureAwait(false);

        AdvanceRoundHandler advancer = new(store);
        for (int round = 1; round <= PartialRounds; round++)
        {
            await advancer.HandleAsync(saveId, leagueId).ConfigureAwait(false);
        }

        await CorruptPersistedRoundAsync(store, saveId, leagueId, roundNumber: 2).ConfigureAwait(false);
        return (saveId, leagueId);
    }

    private static async Task CorruptPersistedRoundAsync(
        SaveStore store,
        Guid saveId,
        int leagueId,
        int roundNumber)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RoundEntity round = await context.Rounds
            .SingleAsync(e => e.LeagueId == leagueId && e.StageNumber == 1 && e.RoundNumber == roundNumber)
            .ConfigureAwait(false);
        RoundPayloadDocument payload = RoundPayloadCodec.DecodeRound(round.PayloadJson);
        RoundPayloadEntry[] placements = [.. payload.Placements];
        placements[0] = placements[0] with { BaseThousandths = placements[0].BaseThousandths + 1 };
        round.PayloadJson = RoundPayloadCodec.Encode((payload with { Placements = placements }).ToJson());
        await context.SaveChangesAsync().ConfigureAwait(false);
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-complete-stage-corruption-http-" + Guid.NewGuid().ToString("N"));
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
