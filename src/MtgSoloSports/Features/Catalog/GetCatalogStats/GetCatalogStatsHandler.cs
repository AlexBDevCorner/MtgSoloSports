using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Catalog.GetCatalogStats;

/// <summary>
/// Read-only catalog inventory: persisted unique-athlete counts per sporting
/// color plus the 256-per-color save-readiness flag. Never touches save databases.
/// </summary>
public sealed class GetCatalogStatsHandler
{
    private readonly CatalogStore _store;

    public GetCatalogStatsHandler(CatalogStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetCatalogStatsResponse> HandleAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<SportingColor, int> counts = await _store.GetCountsAsync(cancellationToken).ConfigureAwait(false);
        int total = 0;
        foreach (int count in counts.Values)
        {
            total += count;
        }

        return new GetCatalogStatsResponse(total, counts, Features.Catalog.ImportCatalog.CatalogQuotas.IsSufficient(counts));
    }
}
