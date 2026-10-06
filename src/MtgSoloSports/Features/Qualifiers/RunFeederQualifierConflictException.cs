namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Thrown when a feeder qualifier cannot run in the current lifecycle state
/// (already resolved, prerequisites missing, no pending transition).
/// Maps to HTTP 409.
/// </summary>
public sealed class RunFeederQualifierConflictException : Exception
{
    public RunFeederQualifierConflictException(string message)
        : base(message)
    {
    }
}
