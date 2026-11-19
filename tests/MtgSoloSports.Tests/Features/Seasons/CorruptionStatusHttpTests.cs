using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Seasons;

/// <summary>
/// MSS-144 regression proof for the season-status failure class. A save whose
/// schema predates the Chaos and Cup selection formula activations still
/// returns the legacy status defaults (200) through the real pipeline, while
/// a schema defect on a migrated save and a corrupt activation row propagate
/// loudly as 500 instead of silently reporting a healthy legacy save.
/// </summary>
public sealed class CorruptionStatusHttpTests
{
    [Fact]
    public async Task PreChaosSchema_SeasonStatus_StillReturnsLegacyDefaults()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(SavesRootOf(root));
            DropTable(factory, saveId, "ChaosCupActivations");
            DropTable(factory, saveId, "CupSelectionFormulaActivations");

            using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/season-status");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            StatusPayload? status = await response.Content.ReadFromJsonAsync<StatusPayload>();
            status.ShouldNotBeNull();
            // Legacy chain: no Chaos steps are advertised for pre-Chaos saves.
            status.ChaosEligible.ShouldBeFalse();
            status.LegalNextActions.ShouldBe(["ResolveInauguralMovement"]);
            status.ComputedPhase.ShouldNotBeNull();
            // Legacy formula defaults: no activation edition is reported.
            status.SelectionFormulaEffectiveFromSeasonNumber.ShouldBe(0);
            status.SelectionFormulaVersion.ShouldBe(0);
            status.SelectionBonusWeightPermille.ShouldBe(350);
            status.SelectionPerformanceWeightPermille.ShouldBe(300);
            status.SelectionFormWeightPermille.ShouldBe(200);
            status.SelectionChaosWeightPermille.ShouldBe(50);
            status.SelectionPrestigeWeightPermille.ShouldBe(100);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MissingColumnOnMigratedSave_SeasonStatus_FailsLoudlyAs500()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(SavesRootOf(root));

            // A genuine schema defect on an already-migrated save: the
            // activation backfill must not swallow it as a "predates" condition.
            DropColumn(factory, saveId, "ChaosCupActivations", "ChaosFormatVersion");

            using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/season-status");
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CorruptActivationRow_SeasonStatus_Returns500WithCorruptionMessage()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            Guid saveId = await SharedSaveTemplates.SeedSeason1CompleteAsync(SavesRootOf(root));

            SaveStore store = factory.Services.GetRequiredService<SaveStore>();
            using (SaveDbContext context = store.OpenDbContext(saveId))
            {
                ChaosCupActivationEntity activation = await context.ChaosCupActivations.SingleAsync(e => e.Id == 1);
                activation.ChaosFormatVersion = 999;
                await context.SaveChangesAsync();
            }

            using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/season-status");
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
            ErrorPayload? payload = await response.Content.ReadFromJsonAsync<ErrorPayload>();
            payload.ShouldNotBeNull();
            payload.Error.ShouldNotBeNullOrWhiteSpace();
            payload.Error.ShouldContain("Chaos activation format version", Case.Insensitive);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static string SavePath(WebApplicationFactory<Program> factory, Guid saveId)
    {
        SaveStore store = factory.Services.GetRequiredService<SaveStore>();
        return store.GetSaveFilePath(saveId);
    }

    private static void DropTable(WebApplicationFactory<Program> factory, Guid saveId, string tableName)
    {
        ExecuteNonQuery(factory, saveId, $"DROP TABLE \"{tableName}\";");
    }

    private static void DropColumn(WebApplicationFactory<Program> factory, Guid saveId, string tableName, string columnName)
    {
        ExecuteNonQuery(factory, saveId, $"ALTER TABLE \"{tableName}\" DROP COLUMN \"{columnName}\";");
    }

    private static void ExecuteNonQuery(WebApplicationFactory<Program> factory, Guid saveId, string sql)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = SavePath(factory, saveId),
            Pooling = false,
        };
        using SqliteConnection connection = new(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    private static string SavesRootOf(string root) => Path.Combine(root, "saves");

    private static (WebApplicationFactory<Program> Factory, string Root) CreateFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-corruption-status-" + Guid.NewGuid().ToString("N"));
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

    internal sealed record StatusPayload(
        Guid SaveId,
        int CurrentSeasonNumber,
        string? PersistedPhase,
        string? ComputedPhase,
        int? SourceSeasonNumber,
        int? NextSeasonNumber,
        bool IsInauguralTransition,
        int GlobalStage,
        bool IsCurrentSeasonComplete,
        bool SeasonComplete,
        bool MovementResolved,
        bool QualifierResolved,
        bool Rebalanced,
        bool CupSelectionResolved,
        bool CupIndividualResolved,
        bool CupTeamResolved,
        bool CupComplete,
        bool ReadyToStartNextSeason,
        string? ExpectedCup,
        IReadOnlyList<string> LegalNextActions,
        string? NextActionDetail,
        bool ChaosEligible,
        bool ChaosSelectionResolved,
        bool ChaosComplete,
        int SelectionFormulaEffectiveFromSeasonNumber,
        int SelectionFormulaVersion,
        int SelectionBonusWeightPermille,
        int SelectionPerformanceWeightPermille,
        int SelectionFormWeightPermille,
        int SelectionChaosWeightPermille,
        int SelectionPrestigeWeightPermille);
}
