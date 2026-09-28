using MtgSoloSports.Features.Catalog.ImportCatalog;

namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// Mockable boundary for Scryfall HTTP. The default implementation streams the
/// advertised bulk file with bounded memory; tests fake this interface with
/// fixtures so ordinary CI never touches live Scryfall.
/// </summary>
public interface IScryfallBulkGateway
{
    /// <summary>
    /// Fetches live bulk-data metadata and selects the preferred dataset
    /// (current <c>default_cards</c> offering when available).
    /// </summary>
    Task<ScryfallImportSource> GetDefaultCardsSourceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and parses the advertised bulk file into raw card records.
    /// Handles the advertised compression/format (currently gzipped JSON Lines)
    /// without buffering the whole dataset into a string.
    /// </summary>
    Task<IReadOnlyList<BulkCardRecord>> DownloadCardsAsync(
        ScryfallImportSource source,
        CancellationToken cancellationToken = default);
}
