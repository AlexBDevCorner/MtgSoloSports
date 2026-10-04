using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtgSoloSports.Features.Athletes.GetRecordHoldings;
using MtgSoloSports.Features.Records;
using MtgSoloSports.Features.Records.GetRecords;
using MtgSoloSports.Features.Simulation.CompleteSeason;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.Tests.Features.Universe;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Athletes;

public sealed class AthleteRecordHoldingsTests
{
    [Fact]
    public async Task Holdings_MatchAuthoritativeCareerRecords_IncludingTies()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Holdings Match", 424201UL, 848402UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            await new CompleteSeasonHandler(store).HandleAsync(saveId);

            GetRecordsResponse records = await new GetRecordsHandler(store).HandleAsync(saveId);
            records.Records.Count.ShouldBe(RecordKey.All.Count);

            RecordEntry feeder = records.Records.Single(r =>
                string.Equals(r.RecordKey, RecordKey.FeederTitles, StringComparison.Ordinal));
            feeder.Value.ShouldBe(1);
            feeder.Holders.Count.ShouldBe(8);

            await AssertTiedHoldersMatchAsync(store, saveId, feeder);
            await AssertSampleMatchesFilteredGlobalAsync(store, saveId, records);
            await AssertVacantNeverReturnedAsync(store, saveId, records, feeder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Holdings_DoNotDecodeScoringPayloads()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Holdings No Payloads", 555007UL, 666008UL, UniverseTestCatalog.Build());
            Guid saveId = created.Detail.SaveId;
            await new CompleteSeasonHandler(store).HandleAsync(saveId);

            int athleteId = await FirstAthleteAsync(store, saveId);
            GetAthleteRecordHoldingsHandler holdingsHandler = new(store);
            GetAthleteRecordHoldingsResponse before =
                await holdingsHandler.HandleAsync(saveId, athleteId);

            await CorruptAllPayloadsAsync(store, saveId);

            GetAthleteRecordHoldingsResponse after =
                await holdingsHandler.HandleAsync(saveId, athleteId);
            after.Holdings.Select(h => h.RecordKey).ShouldBe(before.Holdings.Select(h => h.RecordKey));

            GetRecordsHandler recordsHandler = new(store);
            await Should.ThrowAsync<InvalidOperationException>(
                () => recordsHandler.HandleAsync(saveId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Holdings_UnknownAthlete_ThrowsNotFound()
    {
        var (store, root) = CreateStore();
        try
        {
            SaveStore.CreationRecord created = await store.CreateAsync(
                "Holdings Missing", 11UL, 22UL, UniverseTestCatalog.Build());
            GetAthleteRecordHoldingsHandler handler = new(store);
            await Should.ThrowAsync<AthleteRecordHoldingsNotFoundException>(
                () => handler.HandleAsync(created.Detail.SaveId, 999999));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Holdings_HandlerNeverReferencesScoringLoader()
    {
        string path = FindSourceFile("GetAthleteRecordHoldingsHandler.cs");
        string source = await File.ReadAllTextAsync(path);
        string code = StripComments(source);
        code.ShouldNotContain("ScoreRecordLoader.");
        code.ShouldNotContain("GetRecordsHandler");
        code.ShouldNotContain("PayloadJson");
        code.ShouldNotContain("ColorCupIndividualRounds");
        code.ShouldNotContain("ColorCupTeamRounds");
        code.ShouldNotContain("TypeCupTeamRounds");
        code.ShouldNotContain("QualifierRounds");
        code.ShouldNotContain("context.Rounds");
    }

    [Fact]
    public async Task Holdings_Endpoint_IsRegistered()
    {
        var (factory, root) = CreateApiFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Holdings Api", 2024UL, 3035UL);

            using HttpResponseMessage ok = await client.GetAsync($"/api/saves/{saveId:D}/athletes/1/record-holdings");
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);

            using HttpResponseMessage missing = await client.GetAsync($"/api/saves/{saveId:D}/athletes/999999/record-holdings");
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertTiedHoldersMatchAsync(SaveStore store, Guid saveId, RecordEntry feeder)
    {
        GetAthleteRecordHoldingsHandler holdingsHandler = new(store);
        foreach (RecordHolderEntry holder in feeder.Holders)
        {
            GetAthleteRecordHoldingsResponse holdings =
                await holdingsHandler.HandleAsync(saveId, holder.AthleteId).ConfigureAwait(false);
            holdings.Holdings.Any(h =>
                string.Equals(h.RecordKey, RecordKey.FeederTitles, StringComparison.Ordinal)).ShouldBeTrue();
            RecordHoldingEntry entry = holdings.Holdings.Single(h =>
                string.Equals(h.RecordKey, RecordKey.FeederTitles, StringComparison.Ordinal));
            entry.Value.ShouldBe(feeder.Value);
            entry.Label.ShouldBe(feeder.Label);
            entry.ValueDisplay.ShouldBe(feeder.ValueDisplay);
            entry.IsBonus.ShouldBe(feeder.IsBonus);
        }
    }

    private static async Task AssertSampleMatchesFilteredGlobalAsync(
        SaveStore store, Guid saveId, GetRecordsResponse records)
    {
        GetAthleteRecordHoldingsHandler holdingsHandler = new(store);
        List<int> sample;
        using (SaveDbContext context = store.OpenDbContext(saveId))
        {
            sample = await context.SaveAthletes
                .AsNoTracking()
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .Take(25)
                .ToListAsync().ConfigureAwait(false);
        }

        foreach (int athleteId in sample)
        {
            HashSet<string> expected = records.Records
                .Where(r => !r.IsVacant && r.Holders.Any(h => h.AthleteId == athleteId))
                .Select(r => r.RecordKey)
                .ToHashSet(StringComparer.Ordinal);
            GetAthleteRecordHoldingsResponse actual =
                await holdingsHandler.HandleAsync(saveId, athleteId).ConfigureAwait(false);
            HashSet<string> held = actual.Holdings.Select(h => h.RecordKey).ToHashSet(StringComparer.Ordinal);
            held.ShouldBe(expected);
        }
    }

    private static async Task AssertVacantNeverReturnedAsync(
        SaveStore store, Guid saveId, GetRecordsResponse records, RecordEntry feeder)
    {
        GetAthleteRecordHoldingsHandler holdingsHandler = new(store);
        int holderId = feeder.Holders[0].AthleteId;
        foreach (RecordEntry vacant in records.Records.Where(r => r.IsVacant))
        {
            GetAthleteRecordHoldingsResponse response =
                await holdingsHandler.HandleAsync(saveId, holderId).ConfigureAwait(false);
            response.Holdings.Any(h =>
                string.Equals(h.RecordKey, vacant.RecordKey, StringComparison.Ordinal)).ShouldBeFalse();
        }
    }

    private static async Task<int> FirstAthleteAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        int payloadRows = await context.Rounds.CountAsync().ConfigureAwait(false);
        payloadRows.ShouldBeGreaterThan(0);
        return await context.SaveAthletes.AsNoTracking().Select(e => e.Id).FirstAsync().ConfigureAwait(false);
    }

    private static async Task CorruptAllPayloadsAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        foreach (RoundEntity round in await context.Rounds.ToListAsync().ConfigureAwait(false))
        {
            round.PayloadJson = "corrupt";
        }

        foreach (QualifierRoundEntity qualifier in await context.QualifierRounds.ToListAsync().ConfigureAwait(false))
        {
            qualifier.PayloadJson = "corrupt";
        }

        foreach (ColorCupIndividualRoundEntity cup in await context.ColorCupIndividualRounds.ToListAsync().ConfigureAwait(false))
        {
            cup.PayloadJson = "corrupt";
        }

        foreach (ColorCupTeamRoundEntity team in await context.ColorCupTeamRounds.ToListAsync().ConfigureAwait(false))
        {
            team.PayloadJson = "corrupt";
        }

        foreach (TypeCupTeamRoundEntity type in await context.TypeCupTeamRounds.ToListAsync().ConfigureAwait(false))
        {
            type.PayloadJson = "corrupt";
        }

        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    private static string FindSourceFile(string fileName)
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            string[] matches = Directory.GetFiles(directory.FullName, fileName, SearchOption.AllDirectories);
            string? match = matches.FirstOrDefault(p => p.Contains("GetRecordHoldings", StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Source file '{fileName}' was not found.");
    }

    private static string StripComments(string source)
    {
        System.Text.StringBuilder builder = new(source.Length);
        bool inLineComment = false;
        bool inBlockComment = false;
        for (int i = 0; i < source.Length; i++)
        {
            if (inLineComment)
            {
                if (source[i] == '\n')
                {
                    inLineComment = false;
                    builder.Append(source[i]);
                }

                continue;
            }

            if (inBlockComment)
            {
                if (source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            builder.Append(source[i]);
        }

        return builder.ToString();
    }

    private static (SaveStore Store, string Root) CreateStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-holdings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<SaveStorageOptions> options = Options.Create(new SaveStorageOptions { SavesRoot = root });
        TestHostEnvironment environment = new(root);
        SaveSqliteConnectionInterceptor interceptor = new();
        SaveDbContextFactory factory = new(interceptor);
        SaveStore store = new(options, environment, factory, TimeProvider.System, NullLogger<SaveStore>.Instance);
        return (store, root);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
        }

        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "MtgSoloSports.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static (WebApplicationFactory<Program> Factory, string Root) CreateApiFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-holdings-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.Configure<SaveStorageOptions>(o => o.SavesRoot = Path.Combine(root, "saves"));
                services.Configure<MtgSoloSports.Persistence.Catalog.CatalogStorageOptions>(o => o.CatalogPath = Path.Combine(root, "catalog.db"));
            }));
        return (factory, root);
    }

    private static async Task SeedCatalogAsync(HttpClient client)
    {
        string bulkJson = UniverseTestCatalog.BuildBulkJson();
        using StringContent content = new(bulkJson, Encoding.UTF8, "application/json");
        using HttpResponseMessage imported = await client.PostAsync("/api/catalog/import", content).ConfigureAwait(false);
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<Guid> CreateSaveAsync(HttpClient client, string name, ulong seed, ulong stream)
    {
        using HttpResponseMessage created = await client.PostAsJsonAsync("/api/saves", new { name, seed, stream }).ConfigureAwait(false);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        SavePayload? payload = await created.Content.ReadFromJsonAsync<SavePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload!.SaveId;
    }

    private sealed record SavePayload(Guid SaveId);
}
