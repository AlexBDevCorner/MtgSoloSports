using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtgSoloSports.SaveSnapshot;

/// <summary>
/// Compact machine-readable description of one published working snapshot.
/// Written to <c>snapshot.json</c> next to the validated <c>save.db</c> and
/// printed to stdout by <c>acquire</c>. Logs always go to stderr so stdout
/// stays parseable.
/// </summary>
public sealed record SnapshotInfo(
    string SnapshotPath,
    Guid SaveId,
    string SaveName,
    DateTimeOffset ExportedUtc,
    DateTimeOffset AcquiredUtc,
    int SchemaVersion,
    int RulesVersion,
    string RngAlgorithm,
    int RngVersion,
    string DatabaseSha256,
    int CurrentSeason,
    string Phase,
    IReadOnlyList<SeasonCoverage> Seasons)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static SnapshotInfo FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        SnapshotInfo? info = JsonSerializer.Deserialize<SnapshotInfo>(json, JsonOptions);
        return info ?? throw new InvalidOperationException("Working snapshot metadata payload is empty.");
    }
}
