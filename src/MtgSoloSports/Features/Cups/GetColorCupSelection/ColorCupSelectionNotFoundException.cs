namespace MtgSoloSports.Features.Cups.GetColorCupSelection;

/// <summary>
/// Thrown when no Color Cup team selection has been resolved yet. Maps to 404.
/// </summary>
public sealed class ColorCupSelectionNotFoundException : Exception
{
    public ColorCupSelectionNotFoundException(string message)
        : base(message)
    {
    }
}
