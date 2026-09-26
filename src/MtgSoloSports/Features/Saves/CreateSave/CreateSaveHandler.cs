using System.Globalization;
using MtgSoloSports.Features.Catalog.ImportCatalog;
using MtgSoloSports.Persistence.Catalog;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Saves.CreateSave;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Creates the save database file
/// with metadata, immutable Rules v1 snapshot, the deterministic 2,048-athlete
/// universe copied from the catalog, and the post-selection RNG state in one
/// transaction. An insufficient catalog aborts with no partial save left behind.
/// </summary>
public sealed class CreateSaveHandler
{
    private readonly SaveStore _store;
    private readonly CatalogStore _catalog;

    public CreateSaveHandler(SaveStore store, CatalogStore catalog)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<CreateSaveResponse> HandleAsync(CreateSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<CatalogAthlete> candidates = await _catalog.ListAthletesAsync(cancellationToken).ConfigureAwait(false);
        SaveStore.CreationRecord created = await _store.CreateAsync(request.Name, request.Seed, request.Stream, candidates, cancellationToken).ConfigureAwait(false);
        _ = SavePhaseParser.Parse(created.Detail.Phase);

        Dictionary<string, int> athletesPerColor = new(StringComparer.Ordinal);
        foreach (SportingColor color in Enum.GetValues<SportingColor>())
        {
            athletesPerColor[color.ToString()] = created.Universe.AthletesPerColor.TryGetValue(color, out int count) ? count : 0;
        }

        return new CreateSaveResponse(
            created.Detail.SaveId,
            created.Detail.Name,
            created.Detail.CreatedUtc,
            created.Detail.SchemaVersion,
            created.Detail.CurrentSeason,
            created.Detail.Phase,
            created.Detail.RngAlgorithm,
            created.Detail.RngVersion,
            created.Detail.RngState.ToString(CultureInfo.InvariantCulture),
            created.Detail.RngStream.ToString(CultureInfo.InvariantCulture),
            created.Detail.RulesVersion,
            created.Universe.TotalAthletes,
            athletesPerColor,
            created.Universe.Checksum);
    }
}
