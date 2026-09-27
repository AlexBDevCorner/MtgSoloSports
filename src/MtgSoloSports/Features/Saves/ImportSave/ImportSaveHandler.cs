using System.Globalization;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.ImportSave;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Imports a portable save
/// artifact after validating archive shape, checksum, identity, and
/// schema/rules compatibility. Never silently overwrites another save:
/// an existing save id requires explicit <paramref name="overwrite"/>.
/// </summary>
public sealed class ImportSaveHandler
{
    private readonly SaveStore _store;

    public ImportSaveHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ImportSaveResponse> HandleAsync(
        byte[] bundleBytes,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundleBytes);
        SaveStore.SaveDetailRecord detail = await _store.ImportAsync(bundleBytes, overwrite, cancellationToken).ConfigureAwait(false);
        return new ImportSaveResponse(
            detail.SaveId,
            detail.Name,
            detail.CreatedUtc,
            detail.SchemaVersion,
            detail.CurrentSeason,
            detail.Phase,
            detail.RngAlgorithm,
            detail.RngVersion,
            detail.RngState.ToString(CultureInfo.InvariantCulture),
            detail.RngStream.ToString(CultureInfo.InvariantCulture),
            detail.RulesVersion);
    }
}
