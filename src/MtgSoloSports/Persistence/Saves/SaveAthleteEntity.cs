using System.Text.Json;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One save-owned athlete snapshot: one unique card name selected into the
/// save universe. All card metadata is copied from the catalog at creation so
/// later catalog changes cannot rewrite history; future sporting simulation
/// reads this table and never the global catalog.
/// Creature types stay a compact immutable JSON array while sporting color is
/// a normalized indexed column for pool draws. Artwork is referenced by URL
/// plus printing set code, never downloaded.
/// </summary>
public sealed class SaveAthleteEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SportingColor { get; set; }

    public string CreatureTypesJson { get; set; } = "[]";

    public string FrontColors { get; set; } = string.Empty;

    public string ManaCost { get; set; } = string.Empty;

    public string TypeLine { get; set; } = string.Empty;

    public string? ImageUrl { get; set; }

    public string? SetCode { get; set; }

    public bool IsArtifact { get; set; }

    public bool HasDevoid { get; set; }

    public bool HasHybridMana { get; set; }

    public int Status { get; set; }

    /// <summary>
    /// Permanent Type Cup nationality (Game Rules §15). Null means uncapped:
    /// the athlete may initially represent any printed creature type. Once an
    /// athlete actually participates in a Type Cup for a type, that type becomes
    /// permanent and the athlete may never represent another type. Allocation
    /// previews and team selections never set this; only actual participation
    /// (a later slice) sets it atomically with the event.
    /// </summary>
    public string? TypeCupNationality { get; set; }

    /// <summary>
    /// Copies catalog metadata into a save-owned row. Every selected athlete
    /// starts in its color's common pool; Season 1 league draws happen later.
    /// </summary>
    public static SaveAthleteEntity FromCatalog(CatalogAthlete athlete)
    {
        ArgumentNullException.ThrowIfNull(athlete);
        return new SaveAthleteEntity
        {
            Name = athlete.Name,
            SportingColor = (int)athlete.SportingColor,
            CreatureTypesJson = JsonSerializer.Serialize(athlete.CreatureTypes),
            FrontColors = string.Concat(athlete.FrontColors),
            ManaCost = athlete.ManaCost,
            TypeLine = athlete.TypeLine,
            ImageUrl = athlete.ImageUrl,
            SetCode = athlete.SetCode,
            IsArtifact = athlete.IsArtifact,
            HasDevoid = athlete.HasDevoid,
            HasHybridMana = athlete.HasHybridMana,
            Status = (int)SaveAthleteStatus.CommonPool,
        };
    }

    /// <summary>
    /// Rehydrates the save-owned snapshot for simulation and history queries.
    /// </summary>
    public CatalogAthlete ToSnapshot()
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
