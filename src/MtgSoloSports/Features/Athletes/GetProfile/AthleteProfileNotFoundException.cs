namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Thrown when an athlete profile does not exist; mapped to 404.
/// </summary>
public sealed class AthleteProfileNotFoundException : Exception
{
    public AthleteProfileNotFoundException(string message)
        : base(message)
    {
    }
}
