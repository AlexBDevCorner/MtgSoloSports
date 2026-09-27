namespace MtgSoloSports.Features.Cups.GetTypeCupSelection;

/// <summary>
/// Thrown when a Type Cup allocation has not been resolved yet. Maps to 404.
/// </summary>
public sealed class TypeCupSelectionNotFoundException : Exception
{
    public TypeCupSelectionNotFoundException(string message)
        : base(message)
    {
    }
}
