namespace MtgSoloSports.Features.Simulation.AdvanceRound;

/// <summary>
/// Thrown when an <c>AdvanceRound</c> request targets an illegal duplicate or
/// out-of-order round: re-executing an already persisted round, skipping a
/// round number, advancing a completed stage, or addressing a league/season
/// that cannot legally advance. Mapped to HTTP 409 Conflict.
/// </summary>
public sealed class AdvanceRoundConflictException : InvalidOperationException
{
    public AdvanceRoundConflictException(string message)
        : base(message)
    {
    }
}
