using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Saves.ListCheckpoints;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Lists verified
/// technical recovery checkpoints for a save, newest last.
/// </summary>
public sealed class ListCheckpointsHandler
{
    private readonly SaveStore _store;

    public ListCheckpointsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListCheckpointsResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        IReadOnlyList<SaveStore.CheckpointRecord> checkpoints = await _store.ListCheckpointsAsync(saveId, cancellationToken).ConfigureAwait(false);
        List<CheckpointEntry> entries = new(checkpoints.Count);
        foreach (SaveStore.CheckpointRecord checkpoint in checkpoints)
        {
            entries.Add(new CheckpointEntry(
                checkpoint.CheckpointId,
                checkpoint.SaveId,
                checkpoint.CreatedUtc,
                checkpoint.Reason,
                checkpoint.SaveName,
                checkpoint.SchemaVersion,
                checkpoint.CurrentSeason,
                checkpoint.Phase,
                checkpoint.DatabaseSha256));
        }

        return new ListCheckpointsResponse(entries);
    }
}
