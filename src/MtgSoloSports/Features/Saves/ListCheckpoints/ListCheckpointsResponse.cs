namespace MtgSoloSports.Features.Saves.ListCheckpoints;

/// <summary>
/// Technical recovery checkpoints for a save, newest last.
/// </summary>
public sealed record ListCheckpointsResponse(IReadOnlyList<CheckpointEntry> Checkpoints);
