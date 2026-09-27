using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.CreateCheckpoint;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Creates a verified
/// technical recovery checkpoint of the last known valid save boundary.
/// Technical recovery only; normal gameplay never rewinds sporting results.
/// </summary>
public sealed class CreateCheckpointHandler
{
    private readonly SaveStore _store;

    public CreateCheckpointHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<CreateCheckpointResponse> HandleAsync(
        Guid saveId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        SaveStore.CheckpointRecord created = await _store.CreateCheckpointAsync(saveId, reason, cancellationToken).ConfigureAwait(false);
        return new CreateCheckpointResponse(
            created.CheckpointId,
            created.SaveId,
            created.CreatedUtc,
            created.Reason,
            created.SaveName,
            created.SchemaVersion,
            created.CurrentSeason,
            created.Phase,
            created.DatabaseSha256);
    }
}
