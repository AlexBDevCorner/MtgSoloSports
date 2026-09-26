namespace MtgSoloSports.Features.Simulation.CompleteStageForAllLeagues;

/// <summary>
/// Thrown when the current global stage cannot be completed for all leagues:
/// the season is already complete or the stage is already complete everywhere.
/// Mapped to HTTP 409 by the endpoint.
/// </summary>
public sealed class CompleteStageForAllLeaguesConflictException : Exception
{
    public CompleteStageForAllLeaguesConflictException(string message)
        : base(message)
    {
    }
}
