namespace MtgSoloSports.Features.Superleague.GetAutomaticMovement;

/// <summary>
/// Thrown when no automatic movement has been resolved yet. Maps to 404.
/// Corrupted sporting state throws <see cref="InvalidOperationException"/> instead.
/// </summary>
public sealed class AutomaticMovementNotFoundException : Exception
{
    public AutomaticMovementNotFoundException(string message)
        : base(message)
    {
    }
}
