namespace MtgSoloSports.Features.Superleague.RunQualifier;

/// <summary>
/// Conflict marker when the Superleague qualifier cannot run:
/// no completed Superleague season ready, automatic movement missing,
/// or the qualifier has already been resolved for the transition.
/// Mapped to HTTP 409 by the endpoint.
/// </summary>
public sealed class RunQualifierConflictException : Exception
{
    public RunQualifierConflictException(string message)
        : base(message)
    {
    }
}
