namespace MtgSoloSports.Features.Cups.GetColorCupTeamResult;

/// <summary>
/// Not-found marker when the Color Cup team event has not been resolved yet
/// for the requested source season. Mapped to HTTP 404.
/// </summary>
public sealed class ColorCupTeamResultNotFoundException : Exception
{
    public ColorCupTeamResultNotFoundException(string message)
        : base(message)
    {
    }
}
