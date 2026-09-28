using System.Text.Json.Serialization;

namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// One bulk-data offering from the metadata endpoint. The current offering
/// advertises <c>jsonl_download_uri</c> (gzipped JSON Lines) plus
/// <c>compressed_size</c>; older <c>download_uri</c> is accepted as a fallback.
/// Field reference: https://scryfall.com/docs/api/bulk-data.
/// </summary>
public sealed class ScryfallBulkItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("download_uri")]
    public string? DownloadUri { get; set; }

    [JsonPropertyName("jsonl_download_uri")]
    public string? JsonlDownloadUri { get; set; }

    [JsonPropertyName("compressed_size")]
    public long? CompressedSize { get; set; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    [JsonPropertyName("content_encoding")]
    public string? ContentEncoding { get; set; }

    /// <summary>
    /// Effective download URL: prefers the current <c>jsonl_download_uri</c>,
    /// falls back to legacy <c>download_uri</c>.
    /// </summary>
    public string? EffectiveDownloadUri =>
        string.IsNullOrWhiteSpace(JsonlDownloadUri) ? DownloadUri : JsonlDownloadUri;
}
