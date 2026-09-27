namespace MtgSoloSports.Features.Simulation.CompleteSeason;

/// <summary>
/// Thrown when the current season cannot be fast-completed because it is
/// already complete or has no legal remaining global stage.
/// Mapped to HTTP 409 by the endpoint.
/// </summary>
public sealed class CompleteSeasonConflictException : Exception
{
    public CompleteSeasonConflictException(string message)
        : base(message)
    {
    }
}
