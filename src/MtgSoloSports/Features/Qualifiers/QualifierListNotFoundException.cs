namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Thrown when no qualifier events have been resolved for the requested
/// transition. Maps to HTTP 404.
/// </summary>
public sealed class QualifierListNotFoundException : Exception
{
    public QualifierListNotFoundException(string message)
        : base(message)
    {
    }
}
