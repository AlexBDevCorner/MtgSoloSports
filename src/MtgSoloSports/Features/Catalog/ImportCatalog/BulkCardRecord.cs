using System.Text.Json.Serialization;

namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// Minimal Scryfall-like bulk card shape used by the catalog importer.
/// Only the fields required for eligibility, front-face extraction and
/// display metadata are modeled; unknown bulk fields are ignored so the
/// importer stays decoupled from live Scryfall evolution.
/// </summary>
public sealed class BulkCardRecord
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("layout")]
    public string? Layout { get; set; }

    [JsonPropertyName("set_type")]
    public string? SetType { get; set; }

    [JsonPropertyName("type_line")]
    public string? TypeLine { get; set; }

    [JsonPropertyName("mana_cost")]
    public string? ManaCost { get; set; }

    [JsonPropertyName("oracle_text")]
    public string? OracleText { get; set; }

    [JsonPropertyName("colors")]
    public IReadOnlyList<string>? Colors { get; set; }

    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; set; }

    [JsonPropertyName("set")]
    public string? Set { get; set; }

    [JsonPropertyName("image_uris")]
    public BulkImageUris? ImageUris { get; set; }

    [JsonPropertyName("card_faces")]
    public IReadOnlyList<BulkCardFace>? CardFaces { get; set; }
}
