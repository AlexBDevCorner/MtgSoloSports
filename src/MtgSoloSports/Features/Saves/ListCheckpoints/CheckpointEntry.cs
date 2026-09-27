namespace MtgSoloSports.Features.Saves.ListCheckpoints;

/// <summary>
/// One verified technical recovery checkpoint.
/// </summary>
public sealed record CheckpointEntry(
    Guid CheckpointId,
    Guid SaveId,
    DateTimeOffset CreatedUtc,
    string Reason,
    string SaveName,
    int SchemaVersion,
    int CurrentSeason,
    string Phase,
    string DatabaseSha256);
