using System.Text.Json;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Persistence.Catalog;

/// <summary>
/// One persisted catalog athlete candidate: one unique card name.
/// Creature types are stored as a compact immutable JSON array; frequently
/// queried sporting color stays a normalized integer column.
/// </summary>
public sealed class CatalogAthleteEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SportingColor { get; set; }

    public string CreatureTypesJson { get; set; } = "[]";

    public bool IsArtifact { get; set; }

    public bool HasDevoid { get; set; }

    public bool HasHybridMana { get; set; }

    public string FrontColors { get; set; } = string.Empty;

    public string ManaCost { get; set; } = string.Empty;

    public string TypeLine { get; set; } = string.Empty;

    public string? ImageUrl { get; set; }

    public string? SetCode { get; set; }

    /// <summary>
    /// Builds an entity from a collapsed athlete candidate.
    /// </summary>
    public static CatalogAthleteEntity FromAthlete(CatalogAthlete athlete)
    {
        ArgumentNullException.ThrowIfNull(athlete);
        return new CatalogAthleteEntity
        {
            Name = athlete.Name,
            SportingColor = (int)athlete.SportingColor,
            CreatureTypesJson = JsonSerializer.Serialize(athlete.CreatureTypes),
            IsArtifact = athlete.IsArtifact,
            HasDevoid = athlete.HasDevoid,
            HasHybridMana = athlete.HasHybridMana,
            FrontColors = string.Concat(athlete.FrontColors),
            ManaCost = athlete.ManaCost,
            TypeLine = athlete.TypeLine,
            ImageUrl = athlete.ImageUrl,
            SetCode = athlete.SetCode,
        };
    }

    /// <summary>
    /// Rehydrates the collapsed candidate, including creature types and colors.
    /// </summary>
    public CatalogAthlete ToAthlete()
    {
        List<string>? types = JsonSerializer.Deserialize<List<string>>(CreatureTypesJson);
        List<string> frontColors = [];
        foreach (char letter in FrontColors)
        {
            frontColors.Add(letter.ToString());
        }

        return new CatalogAthlete(
            Name,
            (SportingColor)SportingColor,
            types ?? [],
            IsArtifact,
            HasDevoid,
            HasHybridMana,
            frontColors,
            ManaCost,
            TypeLine,
            ImageUrl,
            SetCode);
    }
}
