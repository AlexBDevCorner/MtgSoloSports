namespace MtgSoloSports.Features.Saves.CreateCheckpoint;

/// <summary>
/// Verified technical recovery checkpoint descriptor.
/// </summary>
public sealed record CreateCheckpointResponse(
    Guid CheckpointId,
    Guid SaveId,
    DateTimeOffset CreatedUtc,
    string Reason,
    string SaveName,
    int SchemaVersion,
    int CurrentSeason,
    string Phase,
    string DatabaseSha256);
