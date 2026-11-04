using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Cups.CompleteChaosCup;
using MtgSoloSports.Features.Cups.SelectChaosCupField;
using MtgSoloSports.Features.Reports.CupEditionReport;
using MtgSoloSports.Features.Reports.SeasonDossier;
using MtgSoloSports.Features.Reports.UniverseAlmanac;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Cups;
using MtgSoloSports.Tests.Features;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Reports;

/// <summary>
/// MSS-115: Chaos results in Season Dossier and Universe Almanac.
/// Incidental Season 1 history is forked; the Chaos field selection plus
/// bulk completion below are the named actions under test. Dossier and
/// almanac reads below use shared persisted projections (never payloads).
/// </summary>
public sealed class ChaosDossierAlmanacTests
{
    [Fact]
    public async Task SeasonDossier_ChaosComplete_IncludesChampionAndFinal()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-dossier115-");
        try
        {
            await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            await new CompleteChaosCupHandler(store).CompleteCupAsync(saveId, sourceSeasonNumber: 1);

            var handler = new GetSeasonDossierHandler(store);
            (long rngBefore, long streamBefore) = await LoadRngAsync(store, saveId);

            GetSeasonDossierHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, 1, "json");
            string jsonBody = Encoding.UTF8.GetString(jsonFile.Content);
            using JsonDocument doc = JsonDocument.Parse(jsonBody);
            AssertDossierChaosComplete(doc, jsonBody);

            GetSeasonDossierHandler.ReportFile mdFile = await handler.HandleAsync(saveId, 1, "md");
            string mdBody = Encoding.UTF8.GetString(mdFile.Content);
            AssertDossierChaosMarkdown(mdBody, doc);

            await AssertDossierMatchesPersistedAsync(store, saveId, doc);

            GetSeasonDossierHandler.ReportFile second = await handler.HandleAsync(saveId, 1, "json");
            Normalize(jsonBody).ShouldBe(Normalize(Encoding.UTF8.GetString(second.Content)));
            (long rngAfter, long streamAfter) = await LoadRngAsync(store, saveId);
            rngAfter.ShouldBe(rngBefore);
            streamAfter.ShouldBe(streamBefore);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SeasonDossier_NoChaos_StaysTruthful()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-dossier115-absent-");
        try
        {
            var handler = new GetSeasonDossierHandler(store);
            GetSeasonDossierHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, 1, "json");
            string jsonBody = Encoding.UTF8.GetString(jsonFile.Content);
            using JsonDocument doc = JsonDocument.Parse(jsonBody);
            JsonElement chaos = doc.RootElement.GetProperty("chaosCup");
            chaos.GetProperty("state").GetString().ShouldBe("NotHeld");
            chaos.GetProperty("hasEdition").GetBoolean().ShouldBeFalse();
            chaos.GetProperty("isComplete").GetBoolean().ShouldBeFalse();
            chaos.GetProperty("champion").ValueKind.ShouldBe(JsonValueKind.Null);
            chaos.GetProperty("finalTopThree").GetArrayLength().ShouldBe(0);

            GetSeasonDossierHandler.ReportFile mdFile = await handler.HandleAsync(saveId, 1, "md");
            string mdBody = Encoding.UTF8.GetString(mdFile.Content);
            mdBody.ShouldContain("## Chaos Cup");
            mdBody.ShouldContain("not held");

            var almanac = new GetUniverseAlmanacHandler(store);
            GetUniverseAlmanacHandler.ReportFile almanacJson = await almanac.HandleAsync(saveId, "json");
            using JsonDocument almanacDoc = JsonDocument.Parse(Encoding.UTF8.GetString(almanacJson.Content));
            almanacDoc.RootElement.GetProperty("chaosEditions").GetArrayLength().ShouldBe(0);
            almanacDoc.RootElement.GetProperty("chaosMedals").GetArrayLength().ShouldBe(0);
            JsonElement seasons = almanacDoc.RootElement.GetProperty("seasons");
            seasons.EnumerateArray().All(s => string.Equals(s.GetProperty("chaosCupState").GetString(), "NotHeld", StringComparison.Ordinal)).ShouldBeTrue();
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UniverseAlmanac_ChaosComplete_TotalsMatchWithNoDuplicates()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-almanac115-");
        try
        {
            await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);
            await new CompleteChaosCupHandler(store).CompleteCupAsync(saveId, sourceSeasonNumber: 1);

            var handler = new GetUniverseAlmanacHandler(store);
            (long rngBefore, long streamBefore) = await LoadRngAsync(store, saveId);
            GetUniverseAlmanacHandler.ReportFile jsonFile = await handler.HandleAsync(saveId, "json");
            string jsonBody = Encoding.UTF8.GetString(jsonFile.Content);
            using JsonDocument doc = JsonDocument.Parse(jsonBody);
            AssertAlmanacChaosComplete(doc);

            GetUniverseAlmanacHandler.ReportFile mdFile = await handler.HandleAsync(saveId, "md");
            string mdBody = Encoding.UTF8.GetString(mdFile.Content);
            mdBody.ShouldContain("## Chaos Cup editions");
            mdBody.ShouldContain("Chaos Cup medals — all-time by athlete");
            mdBody.ShouldContain("Chaos Cup medals — all-time by sporting color");
            mdBody.ShouldContain("never substitute season totals");

            await AssertAlmanacMatchesPersistedAsync(store, saveId, doc);

            GetUniverseAlmanacHandler.ReportFile second = await handler.HandleAsync(saveId, "json");
            Normalize(jsonBody).ShouldBe(Normalize(Encoding.UTF8.GetString(second.Content)));
            (long rngAfter, long streamAfter) = await LoadRngAsync(store, saveId);
            rngAfter.ShouldBe(rngBefore);
            streamAfter.ShouldBe(streamBefore);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    [Fact]
    public async Task UniverseAlmanac_ChaosPending_ContributesZeroMedals()
    {
        var (store, root, saveId) = await SharedSaveTemplates.ForkSeason1CompleteAsync("mtgsolosports-chaos-pending115-");
        try
        {
            await new SelectChaosCupFieldHandler(store).HandleAsync(saveId, sourceSeasonNumber: 1);

            var dossier = new GetSeasonDossierHandler(store);
            GetSeasonDossierHandler.ReportFile dossierJson = await dossier.HandleAsync(saveId, 1, "json");
            using JsonDocument dossierDoc = JsonDocument.Parse(Encoding.UTF8.GetString(dossierJson.Content));
            JsonElement chaos = dossierDoc.RootElement.GetProperty("chaosCup");
            chaos.GetProperty("state").GetString().ShouldBe("InProgress");
            chaos.GetProperty("hasEdition").GetBoolean().ShouldBeTrue();
            chaos.GetProperty("isComplete").GetBoolean().ShouldBeFalse();
            chaos.GetProperty("champion").ValueKind.ShouldBe(JsonValueKind.Null);

            var almanac = new GetUniverseAlmanacHandler(store);
            GetUniverseAlmanacHandler.ReportFile almanacJson = await almanac.HandleAsync(saveId, "json");
            using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(almanacJson.Content));
            JsonElement editions = doc.RootElement.GetProperty("chaosEditions");
            editions.GetArrayLength().ShouldBe(1);
            editions[0].GetProperty("state").GetString().ShouldBe("InProgress");
            editions[0].GetProperty("isComplete").GetBoolean().ShouldBeFalse();
            editions[0].GetProperty("finalTopThree").GetArrayLength().ShouldBe(0);
            doc.RootElement.GetProperty("chaosMedals").GetArrayLength().ShouldBe(0);
        }
        finally
        {
            TestSaveStores.DeleteRoot(root);
        }
    }

    private static void AssertDossierChaosComplete(JsonDocument doc, string jsonBody)
    {
        jsonBody.ShouldNotContain("globalRank");
        JsonElement chaos = doc.RootElement.GetProperty("chaosCup");
        chaos.GetProperty("state").GetString().ShouldBe("Completed");
        chaos.GetProperty("hasEdition").GetBoolean().ShouldBeTrue();
        chaos.GetProperty("isComplete").GetBoolean().ShouldBeTrue();
        chaos.GetProperty("fieldSize").GetInt32().ShouldBe(ChaosCupFormat.FieldSize);
        chaos.GetProperty("roundsPlayed").GetInt32().ShouldBe(chaos.GetProperty("totalRounds").GetInt32());
        chaos.GetProperty("groupsCompleted").GetInt32().ShouldBe(chaos.GetProperty("totalGroups").GetInt32());
        chaos.GetProperty("editionChecksum").GetString().ShouldNotBeNullOrWhiteSpace();
        JsonElement champion = chaos.GetProperty("champion");
        champion.ValueKind.ShouldNotBe(JsonValueKind.Null);
        champion.GetProperty("finalPlace").GetInt32().ShouldBe(1);
        JsonElement topThree = chaos.GetProperty("finalTopThree");
        topThree.GetArrayLength().ShouldBe(3);
        topThree.EnumerateArray().Select(e => e.GetProperty("rank").GetInt32()).ShouldBe([1, 2, 3]);
        JsonElement byColor = chaos.GetProperty("byColor");
        byColor.GetArrayLength().ShouldBe(8);
        foreach (JsonElement bucket in byColor.EnumerateArray())
        {
            bucket.GetProperty("count").GetInt32().ShouldBe(ChaosCupFormat.EntrantsPerColor);
        }

        JsonElement byDivision = chaos.GetProperty("byDivision");
        int divisionTotal = 0;
        foreach (JsonElement bucket in byDivision.EnumerateArray())
        {
            divisionTotal = checked(divisionTotal + bucket.GetProperty("count").GetInt32());
        }

        divisionTotal.ShouldBe(ChaosCupFormat.FieldSize);
        chaos.GetProperty("note").GetString()!.ShouldContain("no league championship points");
        doc.RootElement.GetProperty("colorCup").GetProperty("state").GetString().ShouldNotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("typeCup").GetProperty("state").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    private static void AssertDossierChaosMarkdown(string mdBody, JsonDocument doc)
    {
        mdBody.ShouldContain("## Chaos Cup");
        mdBody.ShouldContain("isolated per 32-athlete group");
        mdBody.ShouldContain("no league championship points");
        mdBody.ShouldContain("never substitute season totals or weighted career points");
        string champion = doc.RootElement.GetProperty("chaosCup").GetProperty("champion").GetProperty("athleteName").GetString()!;
        mdBody.ShouldContain(champion);
        mdBody.ShouldContain("P1");
        mdBody.ShouldNotContain("no official winner");
    }

    private static async Task AssertDossierMatchesPersistedAsync(SaveStore store, Guid saveId, JsonDocument doc)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> finals = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == ChaosCupFormat.StageCount - 1)
            .OrderBy(e => e.GroupRank)
            .ToListAsync().ConfigureAwait(false);
        finals.Count.ShouldBe(ChaosCupFormat.FinalFieldSize);
        int jsonChampion = doc.RootElement.GetProperty("chaosCup").GetProperty("champion").GetProperty("athleteId").GetInt32();
        jsonChampion.ShouldBe(finals[0].SaveAthleteId);
        List<int> jsonFinal = doc.RootElement.GetProperty("chaosCup").GetProperty("finalTopThree")
            .EnumerateArray().OrderBy(e => e.GetProperty("rank").GetInt32())
            .Select(e => e.GetProperty("athleteId").GetInt32()).ToList();
        jsonFinal.ShouldBe(finals.Take(3).Select(e => e.SaveAthleteId).ToList());
    }

    private static void AssertAlmanacChaosComplete(JsonDocument doc)
    {
        JsonElement editions = doc.RootElement.GetProperty("chaosEditions");
        editions.GetArrayLength().ShouldBe(1);
        JsonElement edition = editions[0];
        edition.GetProperty("sourceSeasonNumber").GetInt32().ShouldBe(1);
        edition.GetProperty("state").GetString().ShouldBe("Completed");
        edition.GetProperty("isComplete").GetBoolean().ShouldBeTrue();
        edition.GetProperty("championAthleteId").ValueKind.ShouldNotBe(JsonValueKind.Null);
        edition.GetProperty("finalTopThree").GetArrayLength().ShouldBe(3);

        JsonElement medals = doc.RootElement.GetProperty("chaosMedals");
        medals.GetArrayLength().ShouldBe(3);
        medals.GetArrayLength().ShouldBe(edition.GetProperty("finalTopThree").GetArrayLength());
        int totalMedals = 0;
        foreach (JsonElement row in medals.EnumerateArray())
        {
            totalMedals = checked(totalMedals + row.GetProperty("gold").GetInt32() + row.GetProperty("silver").GetInt32() + row.GetProperty("bronze").GetInt32());
        }

        totalMedals.ShouldBe(3);

        JsonElement colors = doc.RootElement.GetProperty("chaosColorMedals");
        colors.GetArrayLength().ShouldBe(8);
        int colorMedals = 0;
        foreach (JsonElement row in colors.EnumerateArray())
        {
            colorMedals = checked(colorMedals + row.GetProperty("gold").GetInt32() + row.GetProperty("silver").GetInt32() + row.GetProperty("bronze").GetInt32());
        }

        colorMedals.ShouldBe(3);

        JsonElement seasons = doc.RootElement.GetProperty("seasons");
        seasons.EnumerateArray().Single(s => s.GetProperty("seasonNumber").GetInt32() == 1)
            .GetProperty("chaosCupState").GetString().ShouldBe("Completed");
        doc.RootElement.GetProperty("cupEditions").ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetProperty("colorTeamTable").ValueKind.ShouldBe(JsonValueKind.Array);
    }

    private static async Task AssertAlmanacMatchesPersistedAsync(SaveStore store, Guid saveId, JsonDocument doc)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        SeasonEntity source = await context.Seasons.AsNoTracking().SingleAsync(e => e.SeasonNumber == 1).ConfigureAwait(false);
        List<ChaosCupGroupStandingEntity> finals = await context.ChaosCupGroupStandings
            .AsNoTracking()
            .Where(e => e.SourceSeasonId == source.Id && e.StageIndex == ChaosCupFormat.StageCount - 1)
            .OrderBy(e => e.GroupRank)
            .ToListAsync().ConfigureAwait(false);
        JsonElement edition = doc.RootElement.GetProperty("chaosEditions")[0];
        edition.GetProperty("championAthleteId").GetInt32().ShouldBe(finals[0].SaveAthleteId);
        List<int> jsonMedalists = doc.RootElement.GetProperty("chaosMedals")
            .EnumerateArray().Select(e => e.GetProperty("athleteId").GetInt32()).OrderBy(id => id).ToList();
        jsonMedalists.ShouldBe(finals.Take(3).Select(e => e.SaveAthleteId).OrderBy(id => id).ToList());
    }

    private static string Normalize(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        using var stream = new System.IO.MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        WriteNormalized(writer, doc.RootElement);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNormalized(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (string.Equals(prop.Name, "generatedUtc", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writer.WritePropertyName(prop.Name);
                    WriteNormalized(writer, prop.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteNormalized(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static async Task<(long State, long Stream)> LoadRngAsync(SaveStore store, Guid saveId)
    {
        using SaveDbContext context = store.OpenDbContext(saveId);
        RngStateEntity rng = await context.RngStates.AsNoTracking().SingleAsync(e => e.Id == 1).ConfigureAwait(false);
        return (rng.State, rng.Stream);
    }
}
