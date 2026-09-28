namespace MtgSoloSports.Features.Catalog.ImportFromScryfall;

/// <summary>
/// Thrown when the downloaded dataset is malformed or uses an unsupported format.
/// Mapped to HTTP 400. The existing catalog is left untouched.
/// </summary>
public sealed class ScryfallImportFailedException : InvalidOperationException
{
    public ScryfallImportFailedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
