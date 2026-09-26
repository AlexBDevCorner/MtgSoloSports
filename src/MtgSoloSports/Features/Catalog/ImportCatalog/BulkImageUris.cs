using System.Text.Json.Serialization;

namespace MtgSoloSports.Features.Catalog.ImportCatalog;

/// <summary>
/// Artwork links for one printing or face. Only display metadata is retained.
/// </summary>
public sealed class BulkImageUris
{
    [JsonPropertyName("small")]
    public string? Small { get; set; }

    [JsonPropertyName("normal")]
    public string? Normal { get; set; }

    [JsonPropertyName("large")]
    public string? Large { get; set; }
}
