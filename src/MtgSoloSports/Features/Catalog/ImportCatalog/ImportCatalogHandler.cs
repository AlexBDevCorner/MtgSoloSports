using MtgSoloSports.Persistence.Catalog;

namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Ingests bulk JSON, filters to
/// eligible creatures, collapses printings by card name, classifies sporting
/// colors from front-face inputs and replaces the shared catalog atomically.
/// </summary>
public sealed class ImportCatalogHandler
{
    private readonly CatalogStore _store;

    public ImportCatalogHandler(CatalogStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ImportCatalogResponse> HandleAsync(ImportCatalogRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.BulkJson))
        {
            throw new ArgumentException("Bulk JSON must not be empty.", nameof(request));
        }

        IReadOnlyList<BulkCardRecord> records = BulkCatalogParser.ParseJson(request.BulkJson);
        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(records);
        CatalogImportResult result = await _store.ImportAsync(records, athletes, cancellationToken).ConfigureAwait(false);
        return ImportCatalogResponse.FromResult(result);
    }

    public async Task<ImportCatalogResponse> HandleAsync(Stream bulkJsonStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bulkJsonStream);

        IReadOnlyList<BulkCardRecord> records = await BulkCatalogParser.ParseJsonAsync(bulkJsonStream, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CatalogAthlete> athletes = BulkCatalogParser.BuildAthletes(records);
        CatalogImportResult result = await _store.ImportAsync(records, athletes, cancellationToken).ConfigureAwait(false);
        return ImportCatalogResponse.FromResult(result);
    }
}
