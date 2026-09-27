namespace MtgSoloSports.Features.Saves.RestoreCheckpoint;

/// <summary>
/// Technical recovery restore result. Mirrors the open-save detail shape so
/// the restored universe behaves exactly like the checkpointed boundary.
/// </summary>
public sealed record RestoreCheckpointResponse(
    Guid SaveId,
    Guid RestoredCheckpointId,
    string Name,
    DateTimeOffset CreatedUtc,
    int SchemaVersion,
    int CurrentSeason,
    string Phase,
    string RngAlgorithm,
    int RngVersion,
    string RngState,
    string RngStream,
    int RulesVersion);
