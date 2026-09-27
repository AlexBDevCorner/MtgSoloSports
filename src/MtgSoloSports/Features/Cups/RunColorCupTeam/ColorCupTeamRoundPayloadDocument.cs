using System.Text.Json;
using System.Text.Json.Serialization;
using MtgSoloSports.Features.History;
using MtgSoloSports.Features.Simulation.AdvanceRound;

namespace MtgSoloSports.Features.Cups.RunColorCupTeam;

/// <summary>
/// Compact immutable payload for one persisted Color Cup team round.
/// One <c>ColorCupTeamRound</c> row holds exactly one payload for one
/// (group, round) pair; detailed replay consumes this payload and never
/// resimulates. Placements reuse <see cref="RoundPayloadEntry"/> so scoring
/// validation stays identical to normal league rounds; identity is the source
/// season plus group number plus round number, never a league stage cursor.
/// Stored Brotli-compressed (<c>br1:</c> Base64) in
/// <c>ColorCupTeamRoundEntity.PayloadJson</c> via <c>RoundPayloadCodec</c>,
/// matching normal league round history; readers must decode via
/// <see cref="FromStored"/>.
/// </summary>
public sealed record ColorCupTeamRoundPayloadDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("rulesVersion")] int RulesVersion,
    [property: JsonPropertyName("sourceSeasonNumber")] int SourceSeasonNumber,
    [property: JsonPropertyName("groupNumber")] int GroupNumber,
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

    /// <summary>
    /// Encodes this payload for storage: compact JSON then Brotli
    /// (<c>br1:</c> Base64) via the shared round payload codec.
    /// </summary>
    public string ToStored() => RoundPayloadCodec.Encode(ToJson());

    public static ColorCupTeamRoundPayloadDocument FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ColorCupTeamRoundPayloadDocument? document = JsonSerializer.Deserialize<ColorCupTeamRoundPayloadDocument>(json, JsonOptions);
        return document ?? throw new InvalidOperationException("Color Cup team round payload is empty.");
    }

    /// <summary>
    /// Decodes one stored payload: accepts Brotli (<c>br1:</c>) writes and
    /// plain JSON for backwards compatibility; throws on corrupt content.
    /// </summary>
    public static ColorCupTeamRoundPayloadDocument FromStored(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);
        string json = RoundPayloadCodec.DecodeToJson(stored);
        try
        {
            return FromJson(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Color Cup team round payload is corrupt and cannot be replayed.", ex);
        }
    }
}
