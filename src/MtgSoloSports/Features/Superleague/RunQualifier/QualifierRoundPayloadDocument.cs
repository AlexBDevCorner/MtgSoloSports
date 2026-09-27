using System.Text.Json;
using System.Text.Json.Serialization;
using MtgSoloSports.Features.Simulation.AdvanceRound;

namespace MtgSoloSports.Features.Superleague.RunQualifier;

/// <summary>
/// Compact immutable payload for one persisted qualifier round.
/// One <c>QualifierRound</c> row holds exactly one payload; detailed replay
/// consumes this payload and never resimulates. Placements reuse
/// <see cref="RoundPayloadEntry"/> so scoring validation stays identical to
/// normal league rounds; identity is the postseason transition
/// (from/to season) plus round number, never a league stage cursor.
/// </summary>
public sealed record QualifierRoundPayloadDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("rulesVersion")] int RulesVersion,
    [property: JsonPropertyName("fromSeasonNumber")] int FromSeasonNumber,
    [property: JsonPropertyName("toSeasonNumber")] int ToSeasonNumber,
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

    public static QualifierRoundPayloadDocument FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        QualifierRoundPayloadDocument? document = JsonSerializer.Deserialize<QualifierRoundPayloadDocument>(json, JsonOptions);
        return document ?? throw new InvalidOperationException("Qualifier round payload is empty.");
    }
}
