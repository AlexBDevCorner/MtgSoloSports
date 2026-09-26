namespace MtgSoloSports.Features.Superleague.GetInauguralRoster;

/// <summary>
/// Thrown when the inaugural Superleague roster is requested before the
/// Season 1 transition has run. Maps to 404.
/// </summary>
public sealed class InauguralRosterNotFoundException : Exception
{
    public InauguralRosterNotFoundException(string message)
        : base(message)
    {
    }
}
