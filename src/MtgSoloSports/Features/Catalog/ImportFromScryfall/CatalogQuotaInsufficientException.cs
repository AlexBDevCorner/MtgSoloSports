using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// Thrown when the downloaded dataset cannot meet the eight 256-athlete quotas
/// and the existing catalog is already healthy. The healthy catalog is left
/// intact; per-color shortfalls are surfaced so the UI can explain them.
/// Mapped to HTTP 422 with count details.
/// </summary>
public sealed class CatalogQuotaInsufficientException : InvalidOperationException
{
    public CatalogQuotaInsufficientException(
        string message,
        IReadOnlyDictionary<SportingColor, int> countsBySportingColor,
        IReadOnlyList<SportingColor> insufficient,
        ScryfallImportSource? bulkSource)
        : base(message)
    {
        CountsBySportingColor = countsBySportingColor;
        Insufficient = insufficient;
        BulkSource = bulkSource;
    }

    public IReadOnlyDictionary<SportingColor, int> CountsBySportingColor { get; }

    public IReadOnlyList<SportingColor> Insufficient { get; }

    public ScryfallImportSource? BulkSource { get; }
}
