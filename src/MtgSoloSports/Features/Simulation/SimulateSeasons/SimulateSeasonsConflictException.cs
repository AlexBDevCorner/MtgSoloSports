namespace MtgSoloSports.Features.Simulation.SimulateSeasons;

/// <summary>
/// Thrown when an explicit multi-season simulation cannot run because the
/// request is out of bounds or the save has no legal next lifecycle action.
/// Mapped to HTTP 409 by the endpoint (out-of-range counts map to 400).
/// </summary>
public sealed class SimulateSeasonsConflictException : Exception
{
    public SimulateSeasonsConflictException(string message)
        : base(message)
    {
    }
}
