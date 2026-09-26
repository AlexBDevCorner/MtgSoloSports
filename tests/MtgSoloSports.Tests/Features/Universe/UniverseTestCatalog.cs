using System.Text;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Tests.Features.Universe;

/// <summary>
/// Deterministic synthetic catalogs for universe tests. Direct records keep
/// per-color quotas exact; bulk JSON exercises the real import pipeline with
/// classification inputs that map to the intended sporting color.
/// </summary>
internal static class UniverseTestCatalog
{
    public static IReadOnlyList<CatalogAthlete> Build(int perColor = 260)
    {
        List<CatalogAthlete> athletes = new(perColor * 8);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            for (int i = 0; i < perColor; i++)
            {
                athletes.Add(BuildAthlete(color, i));
            }
        }

        return athletes;
    }

    public static CatalogAthlete BuildAthlete(SportingColor color, int index)
    {
        string name = $"{color} Athlete {index:D4}";
        IReadOnlyList<string> types = (index % 3) switch
        {
            0 => ["Human", "Wizard"],
            1 => ["Elf", "Druid"],
            _ => ["Goblin"],
        };
        (IReadOnlyList<string> FrontColors, string ManaCost, string TypeLine, bool IsArtifact, bool HasDevoid, bool HasHybrid) shape = color switch
        {
            SportingColor.White => (["W"], "{W}", "Creature — Human Wizard", false, false, false),
            SportingColor.Blue => (["U"], "{U}", "Creature — Merfolk Wizard", false, false, false),
            SportingColor.Black => (["B"], "{B}", "Creature — Vampire Rogue", false, false, false),
            SportingColor.Red => (["R"], "{R}", "Creature — Goblin Warrior", false, false, false),
            SportingColor.Green => (["G"], "{G}", "Creature — Elf Druid", false, false, false),
            SportingColor.Multicolor => (["W", "U"], "{W}{U}", "Creature — Human Wizard", false, false, false),
            SportingColor.Hybrid => (["W", "U"], "{W/U}", "Creature — Faerie Rogue", false, false, true),
            _ => ([], "{4}", "Artifact Creature — Golem", true, false, false),
        };
        string? imageUrl = index % 2 == 0 ? $"https://img.test/{color}/{index:D4}.jpg" : null;

        return new CatalogAthlete(
            name,
            color,
            types,
            shape.IsArtifact,
            shape.HasDevoid,
            shape.HasHybrid,
            shape.FrontColors,
            shape.ManaCost,
            shape.TypeLine,
            imageUrl,
            "tst");
    }

    /// <summary>
    /// Bulk JSON with classification inputs mapping to the intended sporting
    /// color, for API/pipeline tests that import before creating a save.
    /// </summary>
    public static string BuildBulkJson(int perColor = 256)
    {
        StringBuilder builder = new("[\n");
        bool first = true;
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            for (int i = 0; i < perColor; i++)
            {
                if (!first)
                {
                    builder.Append(",\n");
                }

                first = false;
                builder.Append(CardJson(color, i));
            }
        }

        builder.Append("\n]");
        return builder.ToString();
    }

    private static string CardJson(SportingColor color, int index)
    {
        (string TypeLine, string ManaCost, string Colors) shape = color switch
        {
            SportingColor.White => ("Creature — Human Soldier", "{W}", "[\"W\"]"),
            SportingColor.Blue => ("Creature — Merfolk Wizard", "{U}", "[\"U\"]"),
            SportingColor.Black => ("Creature — Vampire Rogue", "{B}", "[\"B\"]"),
            SportingColor.Red => ("Creature — Goblin Warrior", "{R}", "[\"R\"]"),
            SportingColor.Green => ("Creature — Elf Druid", "{G}", "[\"G\"]"),
            SportingColor.Multicolor => ("Creature — Human Wizard", "{W}{U}", "[\"W\",\"U\"]"),
            SportingColor.Hybrid => ("Creature — Faerie Rogue", "{W/U}", "[\"W\",\"U\"]"),
            _ => ("Artifact Creature — Golem", "{4}", "[]"),
        };
        string imageJson = index % 2 == 0 ? $"{{\"normal\": \"https://img.test/{color}/{index:D4}.jpg\"}}" : "null";
        return $"{{\"name\": \"{color} Card {index:D4}\", \"layout\": \"normal\", \"type_line\": \"{shape.TypeLine}\", \"mana_cost\": \"{shape.ManaCost}\", \"colors\": {shape.Colors}, \"keywords\": [], \"oracle_text\": \"\", \"set\": \"tst\", \"image_uris\": {imageJson}}}";
    }
}
