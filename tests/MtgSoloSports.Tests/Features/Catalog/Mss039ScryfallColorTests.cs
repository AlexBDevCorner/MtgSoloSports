using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Features.Universe.CreateUniverse;
using MtgSoloSports.SimulationKernel.Catalog;
using MtgSoloSports.SimulationKernel.Random;
using MtgSoloSports.SimulationKernel.Rules;
using Shouldly;
using Xunit;

namespace MtgSoloSports.Tests.Features.Catalog;

/// <summary>
/// MSS-039 regressions: multi-face Scryfall color classification.
/// Fixtures below are compact sanitized shapes matching the actual relevant
/// Scryfall field placement observed September 2026 (live API):
/// prepare/flip/adventure faces omit "colors" entirely while the top-level
/// "colors" carries the front-face color; transform/modal_dfc faces carry
/// per-face colors while the top level omits them. A missing field (absent key,
/// deserialized as null) must never equal an explicitly empty array (legit
/// Colorless).
/// </summary>
public sealed class Mss039ScryfallColorTests
{
    [Fact]
    public void DefacingDuskmage_PrepareShape_ClassifiesMulticolor_NotColorless()
    {
        // Actual shape: layout prepare, top colors ["B","W"], faces omit colors.
        string json = """
            [
              {
                "name": "Defacing Duskmage // Vandal's Edit",
                "layout": "prepare",
                "type_line": "Creature — Dog Warlock // Instant",
                "mana_cost": "{W}{B} // {1}{W}{B}",
                "colors": ["B", "W"],
                "color_identity": ["B", "W"],
                "keywords": ["Prepared", "Deathtouch"],
                "oracle_text": null,
                "set": "soc",
                "image_uris": {"normal": "https://img/defacing.jpg"},
                "card_faces": [
                  {
                    "object": "card_face",
                    "name": "Defacing Duskmage",
                    "mana_cost": "{W}{B}",
                    "type_line": "Creature — Dog Warlock",
                    "oracle_text": "Deathtouch\nWhenever an opponent draws their second card each turn, this creature becomes prepared.",
                    "power": "2",
                    "toughness": "2"
                  },
                  {
                    "object": "card_face",
                    "name": "Vandal's Edit",
                    "mana_cost": "{1}{W}{B}",
                    "type_line": "Instant",
                    "oracle_text": "Draw two cards. Each player loses 2 life."
                  }
                ]
              }
            ]
            """;

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(1);
        athletes[0].SportingColor.ShouldBe(SportingColor.Multicolor);
        athletes[0].FrontColors.ShouldBe(["W", "B"]);
    }

    [Fact]
    public void HonorboundPage_PrepareShape_ClassifiesWhite_NotColorless()
    {
        // Actual shape: layout prepare, top colors ["W"], faces omit colors.
        string json = """
            [
              {
                "name": "Honorbound Page // Forum's Favor",
                "layout": "prepare",
                "type_line": "Creature — Cat Cleric // Sorcery",
                "mana_cost": "{3}{W} // {W}",
                "colors": ["W"],
                "color_identity": ["W"],
                "keywords": ["First strike", "Prepared"],
                "oracle_text": null,
                "set": "sos",
                "image_uris": {"normal": "https://img/honorbound.jpg"},
                "card_faces": [
                  {
                    "object": "card_face",
                    "name": "Honorbound Page",
                    "mana_cost": "{3}{W}",
                    "type_line": "Creature — Cat Cleric",
                    "oracle_text": "First strike\nThis creature enters prepared.",
                    "power": "3",
                    "toughness": "3"
                  },
                  {
                    "object": "card_face",
                    "name": "Forum's Favor",
                    "mana_cost": "{W}",
                    "type_line": "Sorcery",
                    "oracle_text": "Target creature gets +1/+0 and gains flying until end of turn."
                  }
                ]
              }
            ]
            """;

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(1);
        athletes[0].SportingColor.ShouldBe(SportingColor.White);
    }

    [Fact]
    public void Sasaya_FlipShape_ClassifiesGreen_NotColorless()
    {
        // Actual shape: layout flip, top colors ["G"], faces omit colors.
        string json = """
            [
              {
                "name": "Sasaya, Orochi Ascendant // Sasaya's Essence",
                "layout": "flip",
                "type_line": "Legendary Creature — Snake Monk // Legendary Enchantment",
                "mana_cost": "{1}{G}{G}",
                "colors": ["G"],
                "color_identity": ["G"],
                "keywords": [],
                "oracle_text": null,
                "power": "2",
                "toughness": "3",
                "set": "sok",
                "image_uris": {"normal": "https://img/sasaya.jpg"},
                "card_faces": [
                  {
                    "object": "card_face",
                    "name": "Sasaya, Orochi Ascendant",
                    "mana_cost": "{1}{G}{G}",
                    "type_line": "Legendary Creature — Snake Monk",
                    "oracle_text": "Reveal your hand: If you have seven or more land cards in your hand, flip Sasaya.",
                    "power": "2",
                    "toughness": "3"
                  },
                  {
                    "object": "card_face",
                    "name": "Sasaya's Essence",
                    "mana_cost": "",
                    "type_line": "Legendary Enchantment",
                    "oracle_text": "Whenever a land you control is tapped for mana, add an additional one mana of any type that land produced."
                  }
                ]
              }
            ]
            """;

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(1);
        athletes[0].SportingColor.ShouldBe(SportingColor.Green);
    }

    [Fact]
    public void Transform_UsesFrontFaceOnly_NeverTopLevelUnion()
    {
        // Valki shape: modal_dfc, top-level omits colors entirely, faces carry
        // per-face colors (front B, back B+R). Must be Black, not Multicolor.
        string json = """
            [
              {
                "name": "Valki, God of Lies // Tibalt, Cosmic Impostor",
                "layout": "modal_dfc",
                "type_line": "Legendary Creature — God // Legendary Planeswalker — Tibalt",
                "color_identity": ["B", "R"],
                "keywords": [],
                "set": "khm",
                "image_uris": null,
                "card_faces": [
                  {
                    "object": "card_face",
                    "name": "Valki, God of Lies",
                    "mana_cost": "{1}{B}",
                    "type_line": "Legendary Creature — God",
                    "oracle_text": "When Valki enters, each opponent reveals their hand.",
                    "colors": ["B"],
                    "power": "2",
                    "toughness": "1"
                  },
                  {
                    "object": "card_face",
                    "name": "Tibalt, Cosmic Impostor",
                    "mana_cost": "{5}{B}{R}",
                    "type_line": "Legendary Planeswalker — Tibalt",
                    "oracle_text": "As Tibalt enters, you get an emblem.",
                    "colors": ["B", "R"]
                  }
                ]
              }
            ]
            """;

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(1);
        athletes[0].SportingColor.ShouldBe(SportingColor.Black);
    }

    [Fact]
    public void Transform_FrontColorlessBackColored_StaysColorless()
    {
        // Synthetic but layout-faithful: transform faces carry per-face colors,
        // front explicitly [] (Colorless artifact creature), back colored.
        string json = """
            [
              {
                "name": "Colorless Front // Colored Back",
                "layout": "transform",
                "type_line": "Artifact Creature — Golem // Creature — Elf",
                "color_identity": ["G"],
                "keywords": [],
                "set": "tst",
                "image_uris": null,
                "card_faces": [
                  {
                    "object": "card_face",
                    "name": "Colorless Front",
                    "mana_cost": "{4}",
                    "type_line": "Artifact Creature — Golem",
                    "oracle_text": "",
                    "colors": [],
                    "power": "3",
                    "toughness": "3"
                  },
                  {
                    "object": "card_face",
                    "name": "Colored Back",
                    "mana_cost": "{2}{G}",
                    "type_line": "Creature — Elf",
                    "oracle_text": "",
                    "colors": ["G"],
                    "power": "4",
                    "toughness": "4"
                  }
                ]
              }
            ]
            """;

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(1);
        athletes[0].SportingColor.ShouldBe(SportingColor.Colorless);
    }

    [Fact]
    public void RealColorless_ExplicitEmptyColors_StaysColorless()
    {
        string json = """
            [
              {"name": "Ornithopter", "layout": "normal", "type_line": "Artifact Creature — Thopter", "mana_cost": "{0}", "colors": [], "keywords": [], "oracle_text": "Flying", "power": "0", "toughness": "2", "set": "m10", "image_uris": null},
              {"name": "Arcane Proxy", "layout": "prototype", "type_line": "Artifact Creature — Wizard", "mana_cost": "{7}", "colors": [], "keywords": ["Prototype"], "oracle_text": "Prototype {1}{U}{U} — 2/1", "power": "4", "toughness": "3", "set": "bro", "image_uris": null},
              {"name": "Phyrexian Fleshgorger", "layout": "prototype", "type_line": "Artifact Creature — Phyrexian Wurm", "mana_cost": "{7}", "colors": [], "keywords": ["Prototype"], "oracle_text": "Prototype {1}{B}{B} — 3/3", "power": "7", "toughness": "5", "set": "bro", "image_uris": null}
            ]
            """;

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(3);
        foreach (CatalogAthlete athlete in athletes)
        {
            athlete.SportingColor.ShouldBe(SportingColor.Colorless);
        }
    }

    [Fact]
    public void Devoid_WithColoredMana_StaysColorless()
    {
        // Brood Butcher / Kozilek's Sentinel shapes: normal layout, explicit
        // empty colors plus Devoid, colored mana costs / color identity.
        string json = """
            [
              {"name": "Brood Butcher", "layout": "normal", "type_line": "Creature — Eldrazi Drone", "mana_cost": "{3}{B}{G}", "colors": [], "color_identity": ["B", "G"], "keywords": ["Devoid"], "oracle_text": "Devoid (This card has no color.)", "power": "3", "toughness": "3", "set": "bfz", "image_uris": null},
              {"name": "Kozilek's Sentinel", "layout": "normal", "type_line": "Creature — Eldrazi Drone", "mana_cost": "{1}{R}", "colors": [], "color_identity": ["R"], "keywords": ["Devoid"], "oracle_text": "Devoid (This card has no color.)", "power": "1", "toughness": "4", "set": "bfz", "image_uris": null}
            ]
            """;

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(2);
        foreach (CatalogAthlete athlete in athletes)
        {
            athlete.SportingColor.ShouldBe(SportingColor.Colorless);
            athlete.HasDevoid.ShouldBeTrue();
        }
    }

    [Fact]
    public void ColoredArtifact_FollowsPrintedColor()
    {
        string json = """
            [
              {"name": "Copper Golem", "layout": "normal", "type_line": "Artifact Creature — Golem", "mana_cost": "{4}{R}", "colors": ["R"], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null},
              {"name": "Sphinx of the Guildpact", "layout": "normal", "type_line": "Artifact Creature — Sphinx", "mana_cost": "{7}", "colors": ["W", "U", "B", "R", "G"], "keywords": [], "oracle_text": "Sphinx of the Guildpact is all colors.", "set": "2xm", "image_uris": null}
            ]
            """;

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        ByName(athletes, "Copper Golem").SportingColor.ShouldBe(SportingColor.Red);
        ByName(athletes, "Sphinx of the Guildpact").SportingColor.ShouldBe(SportingColor.Multicolor);
    }

    [Fact]
    public void ColorIndicatorFront_AddsPrintedColor()
    {
        // Keeper of the Crown shape: adventure, faces omit colors, front carries
        // color_indicator ["G"] with a legendary-mana cost {2}{L}.
        string json = """
            [
              {
                "name": "Keeper of the Crown // Coronation of the Wilds",
                "layout": "adventure",
                "type_line": "Creature — Human Noble // Sorcery — Adventure",
                "mana_cost": "{2}{L} // {2}{G}",
                "colors": ["G"],
                "color_indicator": ["G"],
                "color_identity": ["G"],
                "keywords": [],
                "oracle_text": null,
                "set": "tst",
                "image_uris": null,
                "card_faces": [
                  {
                    "object": "card_face",
                    "name": "Keeper of the Crown",
                    "mana_cost": "{2}{L}",
                    "type_line": "Creature — Human Noble",
                    "oracle_text": "Other legendary creatures you control get +1/+1.",
                    "color_indicator": ["G"],
                    "power": "3",
                    "toughness": "4"
                  },
                  {
                    "object": "card_face",
                    "name": "Coronation of the Wilds",
                    "mana_cost": "{2}{G}",
                    "type_line": "Sorcery — Adventure",
                    "oracle_text": "Draw a card."
                  }
                ]
              }
            ]
            """;

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(1);
        athletes[0].SportingColor.ShouldBe(SportingColor.Green);
    }

    [Fact]
    public void AbsentColors_AreRejected_NeverColorless()
    {
        // Single-faced card omitting colors entirely (no key) must be skipped,
        // while an explicitly empty array is legitimate Colorless.
        string json = """
            [
              {"name": "Mystery Creature", "layout": "normal", "type_line": "Creature — Human", "mana_cost": "{W}", "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null},
              {"name": "Real Colorless", "layout": "normal", "type_line": "Artifact Creature — Golem", "mana_cost": "{4}", "colors": [], "keywords": [], "oracle_text": "", "set": "tst", "image_uris": null}
            ]
            """;

        IReadOnlyList<BulkCardRecord> records = BulkCatalogParser.ParseJson(json);
        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(records);

        athletes.Count.ShouldBe(1);
        athletes[0].Name.ShouldBe("Real Colorless");
        athletes[0].SportingColor.ShouldBe(SportingColor.Colorless);

        (int total, int eligible, int unique, int skippedTokens, int skippedNonCreature, int skippedAmbiguous) =
            BulkCatalogParser.CountCollapse(records);
        total.ShouldBe(2);
        eligible.ShouldBe(1);
        unique.ShouldBe(1);
        skippedTokens.ShouldBe(0);
        skippedNonCreature.ShouldBe(0);
        skippedAmbiguous.ShouldBe(1);
    }

    [Fact]
    public void DoubleSided_MissingFaceColorsWithMissingTop_IsRejected()
    {
        // Transform where both the front face and the top level omit colors:
        // must be skipped, never Colorless, and never fall back to top-level.
        string json = """
            [
              {
                "name": "Broken DFC // Broken Back",
                "layout": "transform",
                "type_line": "Creature — Human // Creature — Wolf",
                "color_identity": ["G"],
                "keywords": [],
                "set": "tst",
                "image_uris": null,
                "card_faces": [
                  {
                    "object": "card_face",
                    "name": "Broken DFC",
                    "mana_cost": "{1}{G}",
                    "type_line": "Creature — Human",
                    "oracle_text": "Front."
                  },
                  {
                    "object": "card_face",
                    "name": "Broken Back",
                    "mana_cost": "{2}{G}",
                    "type_line": "Creature — Wolf",
                    "oracle_text": "Back.",
                    "colors": ["G"]
                  }
                ]
              }
            ]
            """;

        IReadOnlyList<BulkCardRecord> records = BulkCatalogParser.ParseJson(json);
        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(records);

        athletes.ShouldBeEmpty();

        (_, _, _, _, _, int skippedAmbiguous) = BulkCatalogParser.CountCollapse(records);
        skippedAmbiguous.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateCollapse_MalformedPrintingNeverWins(bool malformedFirst)
    {
        // Same athlete with one malformed printing (face colors missing, top
        // missing for a transform) and one valid White printing. Regardless of
        // input order, the valid printing must win; the name must never enter
        // Colorless.
        string malformed = """
            {
              "name": "Order Test Card",
              "layout": "transform",
              "type_line": "Creature — Human // Creature — Wolf",
              "color_identity": ["W"],
              "keywords": [],
              "set": "bad",
              "image_uris": null,
              "card_faces": [
                {"object": "card_face", "name": "Order Test Card", "mana_cost": "{W}", "type_line": "Creature — Human", "oracle_text": "Front."},
                {"object": "card_face", "name": "Order Back", "mana_cost": "{W}", "type_line": "Creature — Wolf", "oracle_text": "Back.", "colors": ["W"]}
              ]
            }
            """;
        string valid = """
            {
              "name": "Order Test Card",
              "layout": "normal",
              "type_line": "Creature — Human",
              "mana_cost": "{W}",
              "colors": ["W"],
              "keywords": [],
              "oracle_text": "",
              "set": "good",
              "image_uris": {"normal": "https://img/good.jpg"}
            }
            """;

        string json = malformedFirst ? "[\n" + malformed + ",\n" + valid + "\n]" : "[\n" + valid + ",\n" + malformed + "\n]";

        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));

        athletes.Count.ShouldBe(1);
        athletes[0].Name.ShouldBe("Order Test Card");
        athletes[0].SportingColor.ShouldBe(SportingColor.White);
    }

    [Fact]
    public void ManualAndBulkImports_AgreeOnSameRecords()
    {
        string line1 = """{"name": "Honorbound Page // Forum's Favor", "layout": "prepare", "type_line": "Creature — Cat Cleric // Sorcery", "mana_cost": "{3}{W} // {W}", "colors": ["W"], "color_identity": ["W"], "keywords": ["First strike", "Prepared"], "set": "sos", "image_uris": null, "card_faces": [{"object": "card_face", "name": "Honorbound Page", "mana_cost": "{3}{W}", "type_line": "Creature — Cat Cleric", "oracle_text": "First strike."}, {"object": "card_face", "name": "Forum's Favor", "mana_cost": "{W}", "type_line": "Sorcery", "oracle_text": "Target creature gets +1/+0."}]}""";
        string line2 = """{"name": "Ornithopter", "layout": "normal", "type_line": "Artifact Creature — Thopter", "mana_cost": "{0}", "colors": [], "keywords": [], "oracle_text": "Flying", "set": "m10", "image_uris": null}""";

        IReadOnlyList<BulkCardRecord> arrayRecords = BulkCatalogParser.ParseJson("[\n" + line1 + ",\n" + line2 + "\n]");
        IReadOnlyList<BulkCardRecord> linesRecords = BulkCatalogParser.ParseJsonLines(line1 + "\n" + line2 + "\n");

        IReadOnlyList<CatalogAthlete> fromArray = BulkCatalogParser.BuildAthletes(arrayRecords);
        IReadOnlyList<CatalogAthlete> fromLines = BulkCatalogParser.BuildAthletes(linesRecords);

        fromArray.Count.ShouldBe(fromLines.Count);
        fromArray.Count.ShouldBe(2);
        for (int i = 0; i < fromArray.Count; i++)
        {
            fromArray[i].Name.ShouldBe(fromLines[i].Name);
            fromArray[i].SportingColor.ShouldBe(fromLines[i].SportingColor);
        }

        ByName(fromArray, "Honorbound Page // Forum's Favor").SportingColor.ShouldBe(SportingColor.White);
        ByName(fromArray, "Ornithopter").SportingColor.ShouldBe(SportingColor.Colorless);
    }

    [Fact]
    public void ColorlessPool_ExcludesReportedColoredCards()
    {
        IReadOnlyList<CatalogAthlete> athletes = BuildReportedPlusColorlessCatalog();

        IReadOnlyList<CatalogAthlete> colorless = athletes.Where(a => a.SportingColor == SportingColor.Colorless).ToList();
        ContainsName(colorless, "Defacing Duskmage // Vandal's Edit").ShouldBeFalse();
        ContainsName(colorless, "Honorbound Page // Forum's Favor").ShouldBeFalse();
        ContainsName(colorless, "Sasaya, Orochi Ascendant // Sasaya's Essence").ShouldBeFalse();
        ContainsName(colorless, "Ornithopter").ShouldBeTrue();
        ContainsName(colorless, "Brood Butcher").ShouldBeTrue();
    }

    [Fact]
    public void UniverseSelection_ColorlessLeague_CannotDrawReportedCards()
    {
        List<CatalogAthlete> pool = BuildQuotaPool(BuildReportedPlusColorlessCatalog());

        RulesV1 rules = RulesV1.CreateDefault();
        Pcg32V1 rng = new(1, 2);
        UniverseSelection selection = UniverseSelector.Select(pool, rng, rules);

        IReadOnlyList<CatalogAthlete> selectedColorless = selection.Selected.Where(a => a.SportingColor == SportingColor.Colorless).ToList();
        selectedColorless.Count.ShouldBe(256);
        ContainsName(selectedColorless, "Defacing Duskmage // Vandal's Edit").ShouldBeFalse();
        ContainsName(selectedColorless, "Honorbound Page // Forum's Favor").ShouldBeFalse();
        ContainsName(selectedColorless, "Sasaya, Orochi Ascendant // Sasaya's Essence").ShouldBeFalse();
    }

    private static IReadOnlyList<CatalogAthlete> BuildReportedPlusColorlessCatalog()
    {
        string json = """
            [
              {
                "name": "Defacing Duskmage // Vandal's Edit",
                "layout": "prepare",
                "type_line": "Creature — Dog Warlock // Instant",
                "mana_cost": "{W}{B} // {1}{W}{B}",
                "colors": ["B", "W"],
                "keywords": [],
                "oracle_text": null,
                "set": "soc",
                "image_uris": null,
                "card_faces": [
                  {"object": "card_face", "name": "Defacing Duskmage", "mana_cost": "{W}{B}", "type_line": "Creature — Dog Warlock", "oracle_text": "Deathtouch."},
                  {"object": "card_face", "name": "Vandal's Edit", "mana_cost": "{1}{W}{B}", "type_line": "Instant", "oracle_text": "Draw two cards."}
                ]
              },
              {
                "name": "Honorbound Page // Forum's Favor",
                "layout": "prepare",
                "type_line": "Creature — Cat Cleric // Sorcery",
                "mana_cost": "{3}{W} // {W}",
                "colors": ["W"],
                "keywords": [],
                "oracle_text": null,
                "set": "sos",
                "image_uris": null,
                "card_faces": [
                  {"object": "card_face", "name": "Honorbound Page", "mana_cost": "{3}{W}", "type_line": "Creature — Cat Cleric", "oracle_text": "First strike."},
                  {"object": "card_face", "name": "Forum's Favor", "mana_cost": "{W}", "type_line": "Sorcery", "oracle_text": "Target creature gets +1/+0."}
                ]
              },
              {
                "name": "Sasaya, Orochi Ascendant // Sasaya's Essence",
                "layout": "flip",
                "type_line": "Legendary Creature — Snake Monk // Legendary Enchantment",
                "mana_cost": "{1}{G}{G}",
                "colors": ["G"],
                "keywords": [],
                "oracle_text": null,
                "set": "sok",
                "image_uris": null,
                "card_faces": [
                  {"object": "card_face", "name": "Sasaya, Orochi Ascendant", "mana_cost": "{1}{G}{G}", "type_line": "Legendary Creature — Snake Monk", "oracle_text": "Flip Sasaya."},
                  {"object": "card_face", "name": "Sasaya's Essence", "mana_cost": "", "type_line": "Legendary Enchantment", "oracle_text": "Add mana."}
                ]
              },
              {"name": "Ornithopter", "layout": "normal", "type_line": "Artifact Creature — Thopter", "mana_cost": "{0}", "colors": [], "keywords": [], "oracle_text": "Flying", "set": "m10", "image_uris": null},
              {"name": "Brood Butcher", "layout": "normal", "type_line": "Creature — Eldrazi Drone", "mana_cost": "{3}{B}{G}", "colors": [], "keywords": ["Devoid"], "oracle_text": "Devoid (This card has no color.)", "set": "bfz", "image_uris": null}
            ]
            """;

        return BulkCatalogParser.BuildAthletes(BulkCatalogParser.ParseJson(json));
    }

    private static List<CatalogAthlete> BuildQuotaPool(IReadOnlyList<CatalogAthlete> seed)
    {
        List<CatalogAthlete> pool = [.. seed];
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            int have = pool.Count(a => a.SportingColor == color);
            for (int i = have; i < 256; i++)
            {
                pool.Add(SyntheticAthlete(color, i));
            }
        }

        return pool;
    }

    private static bool ContainsName(IReadOnlyList<CatalogAthlete> athletes, string name)
    {
        foreach (CatalogAthlete athlete in athletes)
        {
            if (string.Equals(athlete.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static CatalogAthlete ByName(IReadOnlyList<CatalogAthlete> athletes, string name) =>
        athletes.Single(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    private static CatalogAthlete SyntheticAthlete(SportingColor color, int index)
    {
        string name = $"Synthetic {color} {index:D4}";
        (IReadOnlyList<string> FrontColors, string ManaCost, string TypeLine) shape = color switch
        {
            SportingColor.White => (["W"], "{W}", "Creature — Human"),
            SportingColor.Blue => (["U"], "{U}", "Creature — Merfolk"),
            SportingColor.Black => (["B"], "{B}", "Creature — Vampire"),
            SportingColor.Red => (["R"], "{R}", "Creature — Goblin"),
            SportingColor.Green => (["G"], "{G}", "Creature — Elf"),
            SportingColor.Multicolor => (["W", "U"], "{W}{U}", "Creature — Human Wizard"),
            SportingColor.Hybrid => (["W", "U"], "{W/U}", "Creature — Ouphe"),
            _ => ([], "{0}", "Artifact Creature — Golem"),
        };
        return new CatalogAthlete(name, color, ["Human"], false, false, color == SportingColor.Hybrid, shape.FrontColors, shape.ManaCost, shape.TypeLine, null, "tst");
    }
}
