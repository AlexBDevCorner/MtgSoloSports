using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// Persisted import outcome: totals plus per-color counts and the
/// save-readiness flag (every color reaches 256 unique athletes).
/// </summary>
public sealed record ImportCatalogResponse(
    int TotalPrintings,
    int EligiblePrintings,
    int UniqueAthletes,
    IReadOnlyDictionary<SportingColor, int> CountsBySportingColor,
    int SkippedTokens,
    int SkippedNonCreature,
    bool IsSufficientForSave)
{
    public static ImportCatalogResponse FromResult(CatalogImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ImportCatalogResponse(
            result.TotalPrintings,
            result.EligiblePrintings,
            result.UniqueAthletes,
            result.CountsBySportingColor,
            result.SkippedTokens,
            result.SkippedNonCreature,
            result.IsSufficientForSave);
    }
}
