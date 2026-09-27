using System.Globalization;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.RestoreCheckpoint;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Restores the last known
/// valid boundary from a verified technical recovery checkpoint after a
/// software bug/failure. This is crash/migration recovery, not gameplay:
/// the normal UI intentionally does not surface checkpoint restore as
/// rewind/resimulate behavior.
/// </summary>
public sealed class RestoreCheckpointHandler
{
    private readonly SaveStore _store;

    public RestoreCheckpointHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<RestoreCheckpointResponse> HandleAsync(
        Guid saveId,
        Guid checkpointId,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (checkpointId == Guid.Empty)
        {
            throw new ArgumentException("Checkpoint id must not be empty.", nameof(checkpointId));
        }

        SaveStore.SaveDetailRecord detail = await _store.RestoreCheckpointAsync(saveId, checkpointId, cancellationToken).ConfigureAwait(false);
        return new RestoreCheckpointResponse(
            detail.SaveId,
            checkpointId,
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
