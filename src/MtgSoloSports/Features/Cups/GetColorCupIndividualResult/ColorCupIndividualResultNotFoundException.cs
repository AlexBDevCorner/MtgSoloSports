namespace MtgSoloSports.Features.Cups.GetColorCupIndividualResult;

/// <summary>
/// Not-found marker when the Color Cup individual event has not been resolved
/// for the requested source season. Mapped to HTTP 404 by the endpoint.
/// </summary>
public sealed class ColorCupIndividualResultNotFoundException : Exception
{
    public ColorCupIndividualResultNotFoundException(string message)
        : base(message)
    {
    }
}
