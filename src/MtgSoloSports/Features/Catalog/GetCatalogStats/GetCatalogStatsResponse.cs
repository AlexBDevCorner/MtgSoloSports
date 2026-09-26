using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.GetCatalogStats;

/// <summary>
/// Catalog inventory snapshot for UI display and save-creation gating.
/// </summary>
/// <param name="TotalAthletes">Persisted unique athletes across all colors.</param>
/// <param name="CountsBySportingColor">Persisted unique athletes per sporting color.</param>
/// <param name="IsSufficientForSave">True when every color reaches 256 athletes.</param>
public sealed record GetCatalogStatsResponse(
    int TotalAthletes,
    IReadOnlyDictionary<SportingColor, int> CountsBySportingColor,
    bool IsSufficientForSave);
