namespace MtgSoloSports.Features.Cups.GetColorCupIndividualProgress;

/// <summary>
/// Not-found marker when no Color Cup individual rounds are persisted for the
/// requested source season. Mapped to HTTP 404 by the endpoint.
/// </summary>
public sealed class GetColorCupIndividualProgressNotFoundException : Exception
{
    public GetColorCupIndividualProgressNotFoundException(string message)
        : base(message)
    {
    }
}
