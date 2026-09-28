namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// Options for the server-mediated Scryfall bulk import.
/// The bulk metadata URI is the stable programmatic entry point
/// (<c>https://api.scryfall.com/bulk-data</c>); timestamped download URLs are
/// discovered at runtime and never hardcoded.
/// </summary>
public sealed class ScryfallBulkOptions
{
    public const string SectionName = "ScryfallBulk";

    /// <summary>
    /// Stable metadata endpoint listing available bulk files.
    /// </summary>
    public string BulkDataUri { get; set; } = "https://api.scryfall.com/bulk-data";

    /// <summary>
    /// Preferred bulk type. Must be <c>default_cards</c> because it preserves
    /// every card object with the fields and artwork required by the catalog parser.
    /// </summary>
    public string PreferredType { get; set; } = "default_cards";
}
