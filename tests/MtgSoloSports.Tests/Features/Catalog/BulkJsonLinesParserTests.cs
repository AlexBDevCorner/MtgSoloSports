using System.Text;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Catalog;

public sealed class BulkJsonLinesParserTests
{
    [Fact]
    public async Task JsonLines_Fixture_ParsesEachLine()
    {
        string jsonl =
            """{"name": "Serra Angel", "layout": "normal", "type_line": "Creature — Angel", "mana_cost": "{3}{W}{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "m10", "image_uris": {"normal": "https://img/serra.jpg"}}""" + "\n" +
            """{"name": "Wind Drake", "layout": "normal", "type_line": "Creature — Drake", "mana_cost": "{1}{U}", "colors": ["U"], "keywords": [], "oracle_text": "", "set": "m10", "image_uris": null}""" + "\n";

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(jsonl), writable: false);
        IReadOnlyList<BulkCardRecord> records = await BulkCatalogParser.ParseJsonLinesAsync(stream);

        records.Count.ShouldBe(2);
        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(records);
        athletes.Count.ShouldBe(2);
    }

    [Fact]
    public async Task JsonLines_BlankLines_AreSkipped()
    {
        string jsonl =
            "\n" +
            """{"name": "Serra Angel", "layout": "normal", "type_line": "Creature — Angel", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "m10", "image_uris": null}""" + "\n" +
            "\n";

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(jsonl), writable: false);
        IReadOnlyList<BulkCardRecord> records = await BulkCatalogParser.ParseJsonLinesAsync(stream);

        records.Count.ShouldBe(1);
    }

    [Fact]
    public async Task JsonLines_MalformedLine_ThrowsInvalidOperation()
    {
        string jsonl =
            """{"name": "Good", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}""" + "\n" +
            "not json\n";

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(jsonl), writable: false);
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(
            () => BulkCatalogParser.ParseJsonLinesAsync(stream));
        ex.Message.ShouldContain("line 2");
    }

    [Fact]
    public async Task JsonLines_Cancelled_ThrowsOperationCanceled()
    {
        string jsonl = """{"name": "Serra Angel", "layout": "normal", "type_line": "Creature — Angel", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "m10", "image_uris": null}""" + "\n";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(jsonl), writable: false);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => BulkCatalogParser.ParseJsonLinesAsync(stream, cts.Token));
    }

    [Fact]
    public void JsonLines_TextHelper_ParsesRepresentativeScryfallShape()
    {
        string jsonl =
            """{"name": "Siege Rhino", "layout": "normal", "type_line": "Creature — Rhino", "mana_cost": "{1}{W}{B}{G}", "colors": ["W", "B", "G"], "keywords": [], "oracle_text": "Trample", "set": "ktk", "image_uris": null, "set_type": "expansion", "unknown_future_field": 123}""" + "\n" +
            """{"name": "Soldier", "layout": "token", "type_line": "Token Creature — Soldier", "mana_cost": "{W}", "colors": ["W"], "keywords": [], "oracle_text": "", "set": "tkt", "image_uris": null}""" + "\n";

        IReadOnlyList<BulkCardRecord> records = BulkCatalogParser.ParseJsonLines(jsonl);
        records.Count.ShouldBe(2);

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(records);
        athletes.Count.ShouldBe(1);
        athletes[0].Name.ShouldBe("Siege Rhino");
    }
}
