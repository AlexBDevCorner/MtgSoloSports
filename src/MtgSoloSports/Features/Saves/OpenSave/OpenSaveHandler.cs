using System.Globalization;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.OpenSave;

public sealed class OpenSaveHandler
{
    private readonly SaveStore _store;

    public OpenSaveHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<OpenSaveResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SaveStore.SaveDetailRecord detail = await _store.OpenAsync(saveId, cancellationToken).ConfigureAwait(false);
        _ = SavePhaseParser.Parse(detail.Phase);
        return new OpenSaveResponse(
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
