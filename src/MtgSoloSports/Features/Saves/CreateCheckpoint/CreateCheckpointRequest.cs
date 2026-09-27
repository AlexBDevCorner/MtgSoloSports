namespace MtgSoloSports.Features.Saves.CreateCheckpoint;

/// <summary>
/// Optional reason describing why the technical recovery checkpoint exists
/// (for example pre-schema-migration or pre-bulk-simulation).
/// </summary>
public sealed record CreateCheckpointRequest(string? Reason);
