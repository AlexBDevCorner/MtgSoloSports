using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// One collapsed athlete candidate: exactly one unique card name.
/// Multiple printings of the same name collapse to this single record while
/// retaining representative display/art metadata for future save snapshots.
/// </summary>
/// <param name="Name">Unique card name; one name is one athlete.</param>
/// <param name="SportingColor">Classified sporting color from front-face inputs.</param>
/// <param name="CreatureTypes">Front-face creature subtypes (for example Human, Wizard).</param>
/// <param name="IsArtifact">True when the front-face type line contains Artifact.</param>
/// <param name="HasDevoid">True when the front face has Devoid.</param>
/// <param name="HasHybridMana">True when the front-face mana cost contains a hybrid symbol.</param>
/// <param name="FrontColors">Normalized front-face printed colors in WUBRG order.</param>
/// <param name="ManaCost">Representative front-face mana cost.</param>
/// <param name="TypeLine">Representative front-face type line.</param>
/// <param name="ImageUrl">Representative artwork URL when the bulk entry supplied one.</param>
/// <param name="SetCode">Representative printing set code when the bulk entry supplied one.</param>
public sealed record CatalogAthlete(
    string Name,
    SportingColor SportingColor,
    IReadOnlyList<string> CreatureTypes,
    bool IsArtifact,
    bool HasDevoid,
    bool HasHybridMana,
    IReadOnlyList<string> FrontColors,
    string ManaCost,
    string TypeLine,
    string? ImageUrl,
    string? SetCode);
