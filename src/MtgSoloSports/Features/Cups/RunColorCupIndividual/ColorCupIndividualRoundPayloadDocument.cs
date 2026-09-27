using System.Text.Json;
using System.Text.Json.Serialization;
using MtgSoloSports.Features.Simulation.AdvanceRound;

namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Compact immutable payload for one persisted Color Cup individual round.
/// One <c>ColorCupIndividualRound</c> row holds exactly one payload; detailed
/// replay consumes this payload and never resimulates. Placements reuse
/// <see cref="RoundPayloadEntry"/> so scoring validation stays identical to
/// normal league rounds; identity is the source season plus round number,
/// never a league stage cursor.
/// </summary>
public sealed record ColorCupIndividualRoundPayloadDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("rulesVersion")] int RulesVersion,
    [property: JsonPropertyName("sourceSeasonNumber")] int SourceSeasonNumber,
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

    public static ColorCupIndividualRoundPayloadDocument FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ColorCupIndividualRoundPayloadDocument? document = JsonSerializer.Deserialize<ColorCupIndividualRoundPayloadDocument>(json, JsonOptions);
        return document ?? throw new InvalidOperationException("Color Cup individual round payload is empty.");
    }
}
