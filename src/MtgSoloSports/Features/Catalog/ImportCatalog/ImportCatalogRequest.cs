namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// Bulk JSON array input for one catalog import. The JSON must be an array of
/// Scryfall-like card objects; no network access is performed.
/// </summary>
/// <param name="BulkJson">Raw bulk JSON array string.</param>
public sealed record ImportCatalogRequest(string BulkJson);
