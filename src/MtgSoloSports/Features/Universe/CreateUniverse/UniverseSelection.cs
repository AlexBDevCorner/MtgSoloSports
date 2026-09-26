using MtgSoloSports.Features.Catalog.ImportCatalog;

namespace MtgSoloSports.Features.Universe.CreateUniverse;

/// <summary>
/// Outcome of one deterministic universe selection. <see cref="Selected"/>
/// holds the save-owned athlete snapshot in deterministic insertion order
/// (sporting-color enum order, selection order within each color) and
/// <see cref="Summary"/> is the UI-facing fingerprint of the same set.
/// </summary>
public sealed record UniverseSelection(
    IReadOnlyList<CatalogAthlete> Selected,
    UniverseCreationSummary Summary);
