namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// Thrown when a second Scryfall import is attempted while one is running.
/// Mapped to HTTP 409 so rapid repeat clicks cannot trigger overlapping imports.
/// </summary>
public sealed class ScryfallImportInProgressException : InvalidOperationException
{
    public ScryfallImportInProgressException()
        : base("A Scryfall catalog import is already running. Please wait for it to finish before retrying.")
    {
    }
}
