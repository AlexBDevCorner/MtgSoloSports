using System.Text.Json.Serialization;

namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// Minimal Scryfall bulk-data list envelope. Unknown fields are ignored so the
/// importer stays decoupled from Scryfall evolution. Current live shape
/// (verified 2026-09-28) is <c>{"object":"list","has_more":false,"data":[...]}</c>.
/// </summary>
public sealed class ScryfallBulkMetadataResponse
{
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("has_more")]
    public bool? HasMore { get; set; }

    [JsonPropertyName("data")]
    public IReadOnlyList<ScryfallBulkItem>? Data { get; set; }
}
