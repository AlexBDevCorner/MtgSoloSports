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

namespace MtgSoloSports.Tests.Features.Cups;

/// <summary>
/// MSS-144 regression proof for the Chaos stage-advance failure class. A
/// corrupt persisted advancement count is a server failure through the real
/// POST draw pipeline (500 with the corruption message), never a client
/// error: the endpoint already maps SaveCorruptionException, and the
/// handler-level invariant guards throw it.
/// </summary>
public sealed class ChaosStageCorruptionHttpTests
{
    [Fact]
    public async Task CorruptAdvancementCount_ChaosStageDraw_Returns500WithCorruptionMessage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(SavesRootOf(root));
            SaveStore store = factory.Services.GetRequiredService<SaveStore>();
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                // Inject a corrupt advancement count into the persisted stage-0
                // standings: one eliminated athlete is flagged as advanced.
                ChaosCupGroupStandingEntity victim = await context.ChaosCupGroupStandings
                    .Where(e => e.StageIndex == 0 && !e.IsAdvanced)
                    .OrderBy(e => e.Id)
                    .FirstAsync();
                victim.IsAdvanced = true;
                await context.SaveChangesAsync();
            }

            using HttpResponseMessage response = await client.PostAsync(
                $"/api/saves/{saveId:D}/cups/chaos/stages/1/draw", content: null);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            ErrorPayload? payload = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            payload.ShouldNotBeNull();
            payload.Error.ShouldNotBeNullOrWhiteSpace();
            payload.Error.ShouldContain("advance", Case.Insensitive);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static string SavesRootOf(string root) => Path.Combine(root, "saves");

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-chaos-corruption-http-" + Guid.NewGuid().ToString("N"));
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
