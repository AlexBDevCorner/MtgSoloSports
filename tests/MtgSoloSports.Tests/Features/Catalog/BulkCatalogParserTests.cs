using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.SimulationKernel.Catalog;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Catalog;

public sealed class BulkCatalogParserTests
{
    [Fact]
    public void MonoColor_Fixture_ClassifiesToMatchingLeague()
    {
        string json = Fixture(
            Card("Serra Angel", "normal", "Creature — Angel", "{3}{W}{W}", ["W"], null, "Flying, vigilance", "m10", "https://img/serra.jpg"),
            Card("Wind Drake", "normal", "Creature — Drake", "{1}{U}", ["U"], null, "Flying", "m10", null),
            Card("Vampire Nighthawk", "normal", "Creature — Vampire Shaman", "{1}{B}{B}", ["B"], null, "Flying", "m10", null),
            Card("Goblin Guide", "normal", "Creature — Goblin Scout", "{R}", ["R"], null, "Haste", "m10", null),
            Card("Llanowar Elves", "normal", "Creature — Elf Druid", "{G}", ["G"], null, "{T}: Add {G}.", "m10", null));

        IReadOnlyList<BulkCardRecord> records = BulkCatalogParser.ParseJson(json);
        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(records);

        athletes.Count.ShouldBe(5);
        ByName(athletes, "Serra Angel").SportingColor.ShouldBe(SportingColor.White);
        ByName(athletes, "Wind Drake").SportingColor.ShouldBe(SportingColor.Blue);
        ByName(athletes, "Vampire Nighthawk").SportingColor.ShouldBe(SportingColor.Black);
        ByName(athletes, "Goblin Guide").SportingColor.ShouldBe(SportingColor.Red);
        ByName(athletes, "Llanowar Elves").SportingColor.ShouldBe(SportingColor.Green);
        ByName(athletes, "Llanowar Elves").CreatureTypes.ShouldBe(["Elf", "Druid"]);
    }

    [Fact]
    public void Multicolor_Fixture_ClassifiesToMulticolor()
    {
        string json = Fixture(
            Card("Siege Rhino", "normal", "Creature — Rhino", "{1}{W}{B}{G}", ["W", "B", "G"], null, "Trample", "ktk", null));

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(1);
        athletes[0].SportingColor.ShouldBe(SportingColor.Multicolor);
        athletes[0].HasHybridMana.ShouldBeFalse();
        athletes[0].HasDevoid.ShouldBeFalse();
    }

    [Fact]
    public void HybridMana_TakesPrecedenceOverMulticolor()
    {
        string json = Fixture(
            Card("Kitchen Finks", "normal", "Creature — Ouphe", "{1}{G/W}{G/W}", ["G", "W"], null, "Persist", "shm", null),
            Card("Boros Reckoner", "normal", "Creature — Minotaur Wizard", "{R/W}{R/W}{R/W}", ["R", "W"], null, "First strike", "gtc", null));

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        ByName(athletes, "Kitchen Finks").SportingColor.ShouldBe(SportingColor.Hybrid);
        ByName(athletes, "Kitchen Finks").HasHybridMana.ShouldBeTrue();
        ByName(athletes, "Boros Reckoner").SportingColor.ShouldBe(SportingColor.Hybrid);
    }

    [Fact]
    public void Devoid_MapsToColorless_EvenWithColors()
    {
        string json = Fixture(
            Card("Thought-Knot Seer", "normal", "Creature — Eldrazi", "{3}{C}", [], ["Devoid"], "Devoid. When this enters, exile a card.", "ogw", null),
            Card("Devoid Tinted Test", "normal", "Creature — Eldrazi", "{2}{U}", ["U"], null, "Devoid. Flying.", "ogw", null));

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        ByName(athletes, "Thought-Knot Seer").SportingColor.ShouldBe(SportingColor.Colorless);
        ByName(athletes, "Thought-Knot Seer").HasDevoid.ShouldBeTrue();
        ByName(athletes, "Devoid Tinted Test").SportingColor.ShouldBe(SportingColor.Colorless);
        ByName(athletes, "Devoid Tinted Test").HasDevoid.ShouldBeTrue();
    }

    [Fact]
    public void ArtifactCreatures_FollowPrintedColor()
    {
        string json = Fixture(
            Card("Ornithopter", "normal", "Artifact Creature — Thopter", "{0}", [], null, "Flying", "m10", null),
            Card("Copper Golem", "normal", "Artifact Creature — Golem", "{4}{R}", ["R"], null, "Trample", "tst", null));

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        CatalogAthlete colorless = ByName(athletes, "Ornithopter");
        colorless.SportingColor.ShouldBe(SportingColor.Colorless);
        colorless.IsArtifact.ShouldBeTrue();
        colorless.CreatureTypes.ShouldBe(["Thopter"]);

        CatalogAthlete colored = ByName(athletes, "Copper Golem");
        colored.SportingColor.ShouldBe(SportingColor.Red);
        colored.IsArtifact.ShouldBeTrue();
    }

    [Fact]
    public void DoubleFaced_UsesFrontFaceOnly()
    {
        string json = """
            [
              {
                "name": "Test DFC // Test Back",
                "layout": "transform",
                "type_line": "Creature — Elf // Sorcery",
                "mana_cost": "{1}{G}",
                "colors": ["G"],
                "keywords": [],
                "oracle_text": "Front.",
                "set": "tst",
                "card_faces": [
                  {
                    "name": "Test DFC",
                    "mana_cost": "{1}{G}",
                    "type_line": "Creature — Elf Warrior",
                    "colors": ["G"],
                    "oracle_text": "Front creature.",
                    "keywords": [],
                    "image_uris": { "normal": "https://img/front.jpg" }
                  },
                  {
                    "name": "Test Back",
                    "mana_cost": "{2}{R}",
                    "type_line": "Sorcery",
                    "colors": ["R"],
                    "oracle_text": "Back spell.",
                    "keywords": [],
                    "image_uris": { "normal": "https://img/back.jpg" }
                  }
                ]
              }
            ]
            """;

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(1);
        athletes[0].Name.ShouldBe("Test DFC // Test Back");
        athletes[0].SportingColor.ShouldBe(SportingColor.Green);
        athletes[0].CreatureTypes.ShouldBe(["Elf", "Warrior"]);
        athletes[0].ImageUrl.ShouldBe("https://img/front.jpg");
    }

    [Fact]
    public void TokensAndNonCreatures_AreExcluded()
    {
        string json = Fixture(
            Card("Soldier", "token", "Token Creature — Soldier", "{W}", ["W"], null, "", "tkt", null),
            Card("Lightning Bolt", "normal", "Instant", "{R}", ["R"], null, "Deal 3 damage.", "m10", null),
            Card("Serra Angel", "normal", "Creature — Angel", "{3}{W}{W}", ["W"], null, "Flying", "m10", null));

        IReadOnlyList<BulkCardRecord> records = BulkCatalogParser.ParseJson(json);
        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(records);

        athletes.Count.ShouldBe(1);
        athletes[0].Name.ShouldBe("Serra Angel");

        (int total, int eligible, int unique, int skippedTokens, int skippedNonCreature, int skippedAmbiguous) = BulkCatalogParser.CountCollapse(records);
        total.ShouldBe(3);
        eligible.ShouldBe(1);
        unique.ShouldBe(1);
        skippedTokens.ShouldBe(1);
        skippedNonCreature.ShouldBe(1);
        skippedAmbiguous.ShouldBe(0);
    }

    [Fact]
    public void MultiplePrintings_CollapseToOneAthlete_RetainingArt()
    {
        string json = Fixture(
            Card("Serra Angel", "normal", "Creature — Angel", "{3}{W}{W}", ["W"], null, "Flying", "m10", "https://img/serra-m10.jpg"),
            Card("Serra Angel", "normal", "Creature — Angel", "{3}{W}{W}", ["W"], null, "Flying", "m11", "https://img/serra-m11.jpg"));

        IReadOnlyList<BulkCardRecord> records = BulkCatalogParser.ParseJson(json);
        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(records);

        athletes.Count.ShouldBe(1);
        athletes[0].Name.ShouldBe("Serra Angel");
        athletes[0].SportingColor.ShouldBe(SportingColor.White);
        athletes[0].ImageUrl.ShouldBe("https://img/serra-m10.jpg");
        athletes[0].SetCode.ShouldBe("m10");

        (int total, int eligible, int unique, _, _, int skippedAmbiguous) = BulkCatalogParser.CountCollapse(records);
        total.ShouldBe(2);
        eligible.ShouldBe(2);
        unique.ShouldBe(1);
        skippedAmbiguous.ShouldBe(0);
    }

    [Fact]
    public void MissingArt_FallsBackToLaterPrinting()
    {
        string json = Fixture(
            Card("Serra Angel", "normal", "Creature — Angel", "{3}{W}{W}", ["W"], null, "Flying", "m10", null),
            Card("Serra Angel", "normal", "Creature — Angel", "{3}{W}{W}", ["W"], null, "Flying", "m11", "https://img/serra-m11.jpg"));

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(1);
        athletes[0].ImageUrl.ShouldBe("https://img/serra-m11.jpg");
    }

    [Fact]
    public void InvalidBulkJson_ThrowsInvalidOperation()
    {
        Should.Throw<InvalidOperationException>(() => BulkCatalogParser.ParseJson("not json"));
        Should.Throw<InvalidOperationException>(() => BulkCatalogParser.ParseJson("{\"name\":\"x\"}"));
    }

    [Fact]
    public void Quotas_Require256PerColor()
    {
        MtgSoloSports.SimulationKernel.Rules.RulesV1 rules = MtgSoloSports.SimulationKernel.Rules.RulesV1.CreateDefault();
        CatalogQuotas.RequiredPerColor.ShouldBe(rules.AthletesPerSportingColor);
        CatalogQuotas.RequiredPerColor.ShouldBe(256);

        Dictionary<SportingColor, int> full = [];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            full[color] = 256;
        }

        CatalogQuotas.IsSufficient(full).ShouldBeTrue();
        Should.NotThrow(() => CatalogQuotas.EnsureSufficient(full));

        full[SportingColor.Red] = 255;
        CatalogQuotas.IsSufficient(full).ShouldBeFalse();
        CatalogQuotas.FindInsufficient(full).ShouldBe([SportingColor.Red]);
        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => CatalogQuotas.EnsureSufficient(full));
        ex.Message.ShouldContain("Red");
        ex.Message.ShouldContain("256");
    }

    private static CatalogAthlete ByName(IReadOnlyList<CatalogAthlete> athletes, string name) =>
        athletes.Single(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    private static string Fixture(params string[] entries) => "[\n" + string.Join(",\n", entries) + "\n]";

    private static string Card(
        string name,
        string layout,
        string typeLine,
        string manaCost,
        string[] colors,
        string[]? keywords,
        string oracleText,
        string set,
        string? imageNormal)
    {
        string colorsJson = "[" + string.Join(",", colors.Select(c => $"\"{c}\"")) + "]";
        string keywordsJson = keywords is null ? "[]" : "[" + string.Join(",", keywords.Select(k => $"\"{k}\"")) + "]";
        string imageJson = imageNormal is null ? "null" : $"{{\"normal\": \"{imageNormal}\"}}";
        string escapedOracle = oracleText.Replace("\"", "\\\"");
        return $"{{\"name\": \"{name}\", \"layout\": \"{layout}\", \"type_line\": \"{typeLine}\", \"mana_cost\": \"{manaCost}\", \"colors\": {colorsJson}, \"keywords\": {keywordsJson}, \"oracle_text\": \"{escapedOracle}\", \"set\": \"{set}\", \"image_uris\": {imageJson}}}";
    }
}
