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
/// MSS-144 regression proof for the season-finalization failure class. A
/// save whose persisted stage standings hold championship points that no
/// longer match the rules scoring table is server-side corruption: the
/// finalizer's kernel accumulation must surface a 500 server failure
/// carrying the corruption message through the real pipeline, never a 400
/// client error, and the invariant failure must abort the finalization
/// mutation without persisting season standings. The trigger state is a
/// forked Season 1 complete template rewound to the finalization boundary
/// (only the highest-id league's Stage 32 is reopened) plus one tampered
/// Stage 1 standing row, so the named action, the finalization chain and
/// the corruption assertion execute in every run.
/// </summary>
public sealed class SeasonCompletionCorruptionHttpTests
{
    [Fact]
    public async Task CorruptPersistedStageStandings_CompleteFinalStage_Returns500WithCorruptionMessage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(SavesRootOf(root));
            int leagueId = await RewindFinalStageAndCorruptStandingsAsync(factory, saveId);

            using HttpResponseMessage response = await client.PostAsync(
                $"/api/saves/{saveId:D}/leagues/{leagueId}/stages/complete", content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            ErrorPayload? payload = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            payload.ShouldNotBeNull();
            payload.Error.ShouldNotBeNullOrWhiteSpace();
            payload.Error.ShouldContain("corrupt stage standings", Case.Insensitive);

            // The invariant failure aborted the mutation: the final stage
            // completion and the season finalization rolled back together.
            using (SaveDbContext context = factory.Services.GetRequiredService<SaveStore>().OpenDbContext(saveId))
            {
                SeasonEntity season = await context.Seasons.SingleAsync(e => e.SeasonNumber == 1);
                season.IsComplete.ShouldBeFalse();
                (await context.SeasonStandings.CountAsync(e => e.SeasonId == season.Id)).ShouldBe(0);
                StageEntity finalStage = await context.Stages
                    .SingleAsync(e => e.SeasonId == season.Id && e.LeagueId == leagueId && e.StageNumber == 32);
                finalStage.IsComplete.ShouldBeFalse();
            }
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    /// <summary>
    /// Rewinds the forked complete-season template to the finalization
    /// boundary: season standings removed, the season-complete flag cleared
    /// and only the highest-id league's Stage 32 reopened (its 16 real
    /// persisted rounds stay, so the endpoint re-runs the genuine completion
    /// chain). One persisted Stage 1 standing of the first league gets
    /// championship points that no longer match the scoring table, so
    /// finalizing the first league fails in the kernel accumulation.
    /// </summary>
    private static async Task<int> RewindFinalStageAndCorruptStandingsAsync(
        WebApplicationFactory<Program> factory,
        Guid saveId)
    {
        SaveStore store = factory.Services.GetRequiredService<SaveStore>();
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity season = await context.Seasons
            .SingleAsync(e => e.SeasonNumber == 1)
            .ConfigureAwait(false);
        context.SeasonStandings.RemoveRange(
            await context.SeasonStandings.Where(e => e.SeasonId == season.Id).ToListAsync().ConfigureAwait(false));
        season.IsComplete = false;

        int lastLeagueId = await context.Leagues
            .Where(e => e.SeasonId == season.Id)
            .OrderByDescending(e => e.Id)
            .Select(e => e.Id)
            .FirstAsync()
            .ConfigureAwait(false);
        StageEntity finalStage = await context.Stages
            .SingleAsync(e => e.SeasonId == season.Id && e.LeagueId == lastLeagueId && e.StageNumber == 32)
            .ConfigureAwait(false);
        finalStage.IsComplete = false;
        context.StageStandings.RemoveRange(
            await context.StageStandings
                .Where(e => e.SeasonId == season.Id && e.LeagueId == lastLeagueId && e.StageNumber == 32)
                .ToListAsync()
                .ConfigureAwait(false));

        int firstLeagueId = await context.Leagues
            .Where(e => e.SeasonId == season.Id)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .FirstAsync()
            .ConfigureAwait(false);
        StageStandingEntity row = await context.StageStandings
            .SingleAsync(e => e.SeasonId == season.Id && e.LeagueId == firstLeagueId && e.StageNumber == 1 && e.StageRank == 1)
            .ConfigureAwait(false);
        row.ChampionshipPointsThousandths += 1;
        await context.SaveChangesAsync().ConfigureAwait(false);
        return lastLeagueId;
    }

    private static string SavesRootOf(string root) => Path.Combine(root, "saves");

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-season-completion-corruption-http-" + Guid.NewGuid().ToString("N"));
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
