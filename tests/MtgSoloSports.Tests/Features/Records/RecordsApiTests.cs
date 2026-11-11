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

namespace MtgSoloSports.Tests.Features.Records;

public sealed class RecordsApiTests
{
    [Fact]
    public async Task RecordsEndpoints_AfterOneSeason_ReturnHonoursRecordsAndHallOfFame()
    {
        var (factory, root) = CreateFactory();
        try
        {
            // MSS-067: seed a shared Season 1 plus postseason save instead of
            // importing the catalog and simulating a season over HTTP; the
            // records/honours/fame endpoints under test read it directly.
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();

            HonoursPayload honours = await FetchHonoursAsync(client, saveId);
            // Season 1 is odd, so SimulateSeasons now includes the post-season Color Cup plus Chaos:
            // 24 feeder leagues x3 podiums (72) + 3 individual podiums + 12 team podiums + 3 Chaos podiums = 90.
            honours.Honours.Count.ShouldBe(90);

            RecordsPayload records = await FetchRecordsAsync(client, saveId);
            records.Records.Count.ShouldBe(13);
            RecordPayload feeder = records.Records.Single(r => string.Equals(r.RecordKey, "feeder_titles", StringComparison.Ordinal));
            feeder.Value.ShouldBe(1);
            feeder.Holders.Count.ShouldBe(24);
            feeder.IsVacant.ShouldBeFalse();
            records.Records.Single(r => string.Equals(r.RecordKey, "superleague_titles", StringComparison.Ordinal)).IsVacant.ShouldBeTrue();
            records.RecentHistory.Count.ShouldBeGreaterThan(0);

            HallOfFamePayload fame = await FetchHallOfFameAsync(client, saveId);
            fame.Leaders.Count.ShouldBeGreaterThan(0);
            fame.Leaders[0].Rank.ShouldBe(1);
            // MSS-106: Chaos runs before promotion and consumes RNG, so the Color Cup
            // winners shift; the points leader is a consistent high placer across
            // league/Chaos/Cup events (0 titles, 0 majors) rather than the pre-Chaos
            // cup double winner.
            // MSS-120: the phased 32-to-16 Color Cup with full carryover shifts the
            // individual champion again; the points leader now holds that major
            // (0 league titles, 1 major).
            fame.Leaders[0].TotalTitles.ShouldBe(0);
            fame.Leaders[0].MajorWins.ShouldBe(1);

            int anyAthlete = honours.Honours[0].AthleteId;
            HonoursPayload filtered = await FetchHonoursByAthleteAsync(client, saveId, anyAthlete);
            filtered.Honours.Count.ShouldBeGreaterThanOrEqualTo(1);
            filtered.Honours.All(h => h.AthleteId == anyAthlete).ShouldBeTrue();
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task HallOfFame_SupportsSeasonRange_PeriodPointsAndValidation()
    {
        var (factory, root) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();

            HallOfFamePayload all = await FetchHallOfFameAsync(client, saveId);
            all.Leaders.Count.ShouldBeGreaterThan(0);
            all.WeightsVersion.ShouldNotBeNullOrWhiteSpace();
            all.MinSeason.ShouldBeLessThanOrEqualTo(1);
            all.MaxSeason.ShouldBeGreaterThanOrEqualTo(1);
            // Points drive the table: every leader has a positive score.
            all.Leaders.All(l => l.CareerPoints > 0).ShouldBeTrue();
            for (int i = 1; i < all.Leaders.Count; i++)
            {
                all.Leaders[i - 1].CareerPoints.ShouldBeGreaterThanOrEqualTo(all.Leaders[i].CareerPoints);
            }

            HallOfFamePayload season = await FetchHallOfFameAsync(client, saveId, "fromSeason=1&toSeason=1");
            season.FromSeason.ShouldBe(1);
            season.ToSeason.ShouldBe(1);
            // Only Season 1 is finalized, so the single-season window matches all time.
            season.Leaders.Count.ShouldBe(all.Leaders.Count);
            season.Leaders[0].AthleteId.ShouldBe(all.Leaders[0].AthleteId);

            HallOfFamePayload empty = await FetchHallOfFameAsync(client, saveId, "fromSeason=50&toSeason=60");
            empty.Leaders.ShouldBeEmpty();

            using HttpResponseMessage invalid = await client.GetAsync($"/api/saves/{saveId:D}/hall-of-fame?fromSeason=8&toSeason=7");
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ScoringRecords_CarryStructuredScopeAndMetric()
    {
        // MSS-140: the League grid places cells from stable ScopeKey/Metric
        // fields, never display labels. Every scoring record carries both.
        var (factory, root) = CreateFactory();
        try
        {
            Guid saveId = await SharedSaveTemplates.SeedSeason1PlusCupsAsync(Path.Combine(root, "saves"));
            using HttpClient client = factory.CreateClient();

            RecordsPayload records = await FetchRecordsAsync(client, saveId);
            records.ScoringRecords.ShouldNotBeNull();
            records.ScoringRecords.Count.ShouldBe(39);

            string[] allowedMetrics = ["round", "stage", "season", "total", "team_round"];
            foreach (ScoringRecordPayload scoring in records.ScoringRecords)
            {
                scoring.ScopeKey.ShouldNotBeNullOrWhiteSpace();
                allowedMetrics.Contains(scoring.Metric, StringComparer.Ordinal).ShouldBeTrue(scoring.RecordKey);
            }

            // League grid coverage: 9 scopes x 3 metrics from structured fields.
            IReadOnlyList<ScoringRecordPayload> league = records.ScoringRecords
                .Where(r => string.Equals(r.Category, "League", StringComparison.Ordinal)).ToList();
            league.Count.ShouldBe(27);
            league.Select(r => r.ScopeKey).Distinct(StringComparer.Ordinal).Count().ShouldBe(9);
            league
                .GroupBy(r => r.ScopeKey, StringComparer.Ordinal)
                .All(g => g.Select(r => r.Metric).OrderBy(m => m, StringComparer.Ordinal)
                    .SequenceEqual(["round", "season", "stage"], StringComparer.Ordinal)).ShouldBeTrue();

            ScoringRecordPayload whiteRound = league.Single(r =>
                string.Equals(r.RecordKey, "league_single_round__white", StringComparison.Ordinal));
            whiteRound.ScopeKey.ShouldBe("white");
            whiteRound.Metric.ShouldBe("round");
            ScoringRecordPayload superPoints = league.Single(r =>
                string.Equals(r.RecordKey, "league_points_best__superleague", StringComparison.Ordinal));
            superPoints.ScopeKey.ShouldBe("superleague");
            superPoints.Metric.ShouldBe("season");
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RecordsApi_Errors_UnknownSaveNotFound()
    {
        var (factory, root) = CreateFactory();
        try
        {
            using HttpClient client = factory.CreateClient();
            await SeedCatalogAsync(client);
            Guid saveId = await CreateSaveAsync(client, "Records Errors", 111003UL, 222004UL);

            using HttpResponseMessage unknownRecords = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/records");
            unknownRecords.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage unknownHonours = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/honours");
            unknownHonours.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage unknownFame = await client.GetAsync($"/api/saves/{Guid.NewGuid():D}/hall-of-fame");
            unknownFame.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using HttpResponseMessage badAthlete = await client.GetAsync($"/api/saves/{saveId:D}/honours?athleteId=0");
            badAthlete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            factory.Dispose();
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static async Task SimulateSeasonsAsync(HttpClient client, Guid saveId, int seasons)
    {
        using HttpResponseMessage simulated = await client.PostAsJsonAsync(
            $"/api/saves/{saveId:D}/simulate-seasons", new { seasons }).ConfigureAwait(false);
        simulated.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<HonoursPayload> FetchHonoursAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/honours").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HonoursPayload? payload = await response.Content.ReadFromJsonAsync<HonoursPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<HonoursPayload> FetchHonoursByAthleteAsync(HttpClient client, Guid saveId, int athleteId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/honours?athleteId={athleteId}").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HonoursPayload? payload = await response.Content.ReadFromJsonAsync<HonoursPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<RecordsPayload> FetchRecordsAsync(HttpClient client, Guid saveId)
    {
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/records").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        RecordsPayload? payload = await response.Content.ReadFromJsonAsync<RecordsPayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
    }

    private static async Task<HallOfFamePayload> FetchHallOfFameAsync(HttpClient client, Guid saveId, string? range = null)
    {
        string suffix = string.IsNullOrEmpty(range) ? "take=20" : $"take=20&{range}";
        using HttpResponseMessage response = await client.GetAsync($"/api/saves/{saveId:D}/hall-of-fame?{suffix}").ConfigureAwait(false);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        HallOfFamePayload? payload = await response.Content.ReadFromJsonAsync<HallOfFamePayload>().ConfigureAwait(false);
        payload.ShouldNotBeNull();
        return payload;
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
        string root = Path.Combine(Path.GetTempPath(), "mtgsolosports-records-api-" + Guid.NewGuid().ToString("N"));
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

    private sealed record HonoursPayload(
        Guid SaveId,
        IReadOnlyList<HonourPayload> Honours);

    private sealed record HonourPayload(
        int SeasonNumber,
        int SeasonId,
        int LeagueId,
        string LeagueName,
        int LeagueKind,
        string HonourKind,
        int AthleteId,
        string AthleteName);

    private sealed record RecordsPayload(
        Guid SaveId,
        IReadOnlyList<RecordPayload> Records,
        IReadOnlyList<RecordHistoryPayload> RecentHistory,
        IReadOnlyList<ScoringRecordPayload>? ScoringRecords = null);

    private sealed record RecordPayload(
        string RecordKey,
        string Label,
        int Value,
        string ValueDisplay,
        bool IsBonus,
        bool IsVacant,
        IReadOnlyList<RecordHolderPayload> Holders);

    private sealed record RecordHolderPayload(int AthleteId, string AthleteName);

    private sealed record RecordHistoryPayload(
        int AthleteId,
        string AthleteName,
        string RecordKey,
        int Value,
        int PriorValue,
        int SeasonNumber,
        string Text);

    private sealed record ScoringRecordPayload(
        string RecordKey,
        string Label,
        string Category,
        string Scope,
        int Value,
        string ValueDisplay,
        bool IsVacant,
        IReadOnlyList<ScoringHolderPayload> Holders,
        string ScopeKey,
        string Metric);

    private sealed record ScoringHolderPayload(
        int? AthleteId,
        string? AthleteName,
        string TeamKey,
        string TeamName,
        int Value,
        string ValueDisplay,
        int SeasonNumber,
        string Competition,
        string? LeagueName,
        int? StageNumber,
        int? RoundNumber,
        int? GroupNumber);

    private sealed record HallOfFamePayload(
        Guid SaveId,
        int Take,
        int TotalAthletes,
        IReadOnlyList<HallOfFameLeaderPayload> Leaders,
        int? FromSeason,
        int? ToSeason,
        int MinSeason,
        int MaxSeason,
        string WeightsVersion);

    private sealed record HallOfFameLeaderPayload(
        int Rank,
        int AthleteId,
        string AthleteName,
        int SportingColor,
        string SportingColorName,
        int FeederTitles,
        int SuperleagueTitles,
        int TotalTitles,
        int StageWins,
        int RoundWins,
        int SuperleagueAppearances,
        int TotalAppearances,
        int LongestSuperleagueTenure,
        int Promotions,
        int Relegations,
        int CurrentEffectiveBonusThousandths,
        int LongestTitleStreak,
        int LongestStageWinStreak,
        int CareerPoints,
        int SuperleaguePoints,
        int Feeder1Points,
        int Feeder2Points,
        int Feeder3Points,
        int ColorCupIndividualPoints,
        int ColorCupTeamPoints,
        int TypeCupTeamPoints,
        int MajorWins,
        int ScoredEvents);
}
