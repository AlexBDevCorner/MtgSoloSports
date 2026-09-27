using System.Text.Json;

namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Sidecar metadata stored next to every technical recovery checkpoint so a
/// checkpoint can be verified (checksum + identity) before any restore.
/// </summary>
public sealed record SaveCheckpointSidecar(
    Guid CheckpointId,
    Guid SaveId,
    DateTimeOffset CreatedUtc,
    string Reason,
    string SaveName,
    int SchemaVersion,
    int CurrentSeason,
    string Phase,
    string DatabaseSha256)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static SaveCheckpointSidecar FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        SaveCheckpointSidecar? sidecar = JsonSerializer.Deserialize<SaveCheckpointSidecar>(json, JsonOptions);
        return sidecar ?? throw new InvalidOperationException("Checkpoint sidecar payload is empty.");
    }
}
