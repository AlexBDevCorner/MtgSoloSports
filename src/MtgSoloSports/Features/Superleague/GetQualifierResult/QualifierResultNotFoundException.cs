namespace MtgSoloSports.Features.Superleague.GetQualifierResult;

/// <summary>
/// Read-model marker when the Superleague qualifier has not been resolved yet.
/// Mapped to HTTP 404 by the endpoint.
/// </summary>
public sealed class QualifierResultNotFoundException : Exception
{
    public QualifierResultNotFoundException(string message)
        : base(message)
    {
    }
}
