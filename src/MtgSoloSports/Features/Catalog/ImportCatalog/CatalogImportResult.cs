using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// Outcome of one catalog import: raw/eligible/unique totals plus per-color
/// counts. The same counts feed the 256-per-color save-creation gate.
/// </summary>
/// <param name="TotalPrintings">Raw bulk entries supplied.</param>
/// <param name="EligiblePrintings">Printings passing creature/token eligibility.</param>
/// <param name="UniqueAthletes">Collapsed unique card names persisted.</param>
/// <param name="CountsBySportingColor">Persisted unique athletes per sporting color.</param>
/// <param name="SkippedTokens">Raw entries rejected as tokens.</param>
/// <param name="SkippedNonCreature">Raw entries rejected as non-creature.</param>
public sealed record CatalogImportResult(
    int TotalPrintings,
    int EligiblePrintings,
    int UniqueAthletes,
    IReadOnlyDictionary<SportingColor, int> CountsBySportingColor,
    int SkippedTokens,
    int SkippedNonCreature)
{
    /// <summary>
    /// True when every sporting color reaches <see cref="CatalogQuotas.RequiredPerColor"/>.
    /// </summary>
    public bool IsSufficientForSave => CatalogQuotas.IsSufficient(CountsBySportingColor);
}
