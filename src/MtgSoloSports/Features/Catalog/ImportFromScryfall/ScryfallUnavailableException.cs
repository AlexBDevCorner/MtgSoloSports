namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// Thrown when Scryfall cannot be reached, returns an error, or times out.
/// Mapped to HTTP 502/504 with an actionable message and retry preserved.
/// </summary>
public sealed class ScryfallUnavailableException : InvalidOperationException
{
    public ScryfallUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
