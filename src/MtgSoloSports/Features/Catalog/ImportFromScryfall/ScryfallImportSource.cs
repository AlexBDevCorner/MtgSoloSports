namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// The selected Scryfall bulk source for one import. Chosen at runtime from
/// live metadata; timestamped URLs are never hardcoded.
/// </summary>
/// <param name="Type">Bulk type, normally <c>default_cards</c>.</param>
/// <param name="Name">Human-readable file name from Scryfall metadata.</param>
/// <param name="UpdatedAt">Last-updated timestamp from Scryfall metadata, when advertised.</param>
/// <param name="DownloadUri">Advertised bulk file URL for this run.</param>
/// <param name="CompressedSize">Advertised compressed size in bytes, when advertised.</param>
public sealed record ScryfallImportSource(
    string Type,
    string? Name,
    string? UpdatedAt,
    string DownloadUri,
    long? CompressedSize)
{
    /// <summary>
    /// True when the advertised file is gzip-compressed JSON Lines
    /// (current Scryfall offering is <c>.jsonl.gz</c>).
    /// </summary>
    public bool IsJsonLines =>
        DownloadUri.Contains("jsonl", StringComparison.OrdinalIgnoreCase);

    public bool IsGzip =>
        DownloadUri.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
}
