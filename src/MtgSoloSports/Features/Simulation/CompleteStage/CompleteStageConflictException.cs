namespace MtgSoloSports.Features.Simulation.CompleteStage;

/// <summary>
/// Thrown when a stage cannot be completed because it is already complete,
/// has no legal rounds, or belongs to an unexpected season/league.
/// Mapped to HTTP 409 by the endpoint.
/// </summary>
public sealed class CompleteStageConflictException : Exception
{
    public CompleteStageConflictException(string message)
        : base(message)
    {
    }
}
