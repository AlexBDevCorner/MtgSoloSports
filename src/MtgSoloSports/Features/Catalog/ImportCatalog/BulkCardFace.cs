using System.Text.Json.Serialization;

namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// One printed face of a single- or double-faced bulk entry.
/// Double-faced cards are evaluated by the caller from index 0 only.
/// </summary>
public sealed class BulkCardFace
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("mana_cost")]
    public string? ManaCost { get; set; }

    [JsonPropertyName("type_line")]
    public string? TypeLine { get; set; }

    [JsonPropertyName("oracle_text")]
    public string? OracleText { get; set; }

    [JsonPropertyName("colors")]
    public IReadOnlyList<string>? Colors { get; set; }

    [JsonPropertyName("color_indicator")]
    public IReadOnlyList<string>? ColorIndicator { get; set; }

    [JsonPropertyName("keywords")]
    public IReadOnlyList<string>? Keywords { get; set; }

    [JsonPropertyName("image_uris")]
    public BulkImageUris? ImageUris { get; set; }
}
