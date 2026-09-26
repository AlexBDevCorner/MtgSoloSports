namespace MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;

/// <summary>
/// Thrown when automatic movement cannot be resolved: no completed Superleague
/// season is ready, or the movement for the completed season already exists.
/// Maps to 409. Corrupted sporting state throws
/// <see cref="InvalidOperationException"/> instead.
/// </summary>
public sealed class ResolveAutomaticMovementConflictException : Exception
{
    public ResolveAutomaticMovementConflictException(string message)
        : base(message)
    {
    }
}
