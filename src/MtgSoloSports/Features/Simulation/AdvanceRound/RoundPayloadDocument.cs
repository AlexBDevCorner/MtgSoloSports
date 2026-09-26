using System.Text.Json;
using System.Text.Json.Serialization;

namespace MtgSoloSports.Features.Simulation.AdvanceRound;

/// <summary>
/// Compact immutable payload for one persisted round. One <c>Round</c> row
/// holds exactly one payload; detailed replay consumes this payload and never
/// resimulates. Stored as compact (non-indented) JSON in
/// <c>RoundEntity.PayloadJson</c>.
/// </summary>
public sealed record RoundPayloadDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("rulesVersion")] int RulesVersion,
    [property: JsonPropertyName("seasonNumber")] int SeasonNumber,
    [property: JsonPropertyName("leagueId")] int LeagueId,
    [property: JsonPropertyName("stageNumber")] int StageNumber,
    [property: JsonPropertyName("roundNumber")] int RoundNumber,
    [property: JsonPropertyName("rngBeforeState")] ulong RngBeforeState,
    [property: JsonPropertyName("rngBeforeStream")] ulong RngBeforeStream,
    [property: JsonPropertyName("rngAfterState")] ulong RngAfterState,
    [property: JsonPropertyName("rngAfterStream")] ulong RngAfterStream,
    [property: JsonPropertyName("checksum")] string Checksum,
    [property: JsonPropertyName("placements")] IReadOnlyList<RoundPayloadEntry> Placements)
{
    public const int PayloadVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RoundPayloadDocument FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        RoundPayloadDocument? document = JsonSerializer.Deserialize<RoundPayloadDocument>(json, JsonOptions);
        return document ?? throw new InvalidOperationException("Round payload is empty.");
    }
}
