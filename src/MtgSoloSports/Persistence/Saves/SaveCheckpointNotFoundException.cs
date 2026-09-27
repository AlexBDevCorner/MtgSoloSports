namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Thrown when a technical recovery checkpoint cannot be found for a save.
/// </summary>
public sealed class SaveCheckpointNotFoundException : InvalidOperationException
{
    public SaveCheckpointNotFoundException(Guid saveId, Guid checkpointId)
        : base($"Checkpoint '{checkpointId:D}' was not found for save '{saveId:D}'.")
    {
        SaveId = saveId;
        CheckpointId = checkpointId;
    }

    public Guid SaveId { get; }

    public Guid CheckpointId { get; }
}
