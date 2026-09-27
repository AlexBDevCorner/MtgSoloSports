namespace MtgSoloSports.Features.Seasons.AdvanceToNextEvent;

/// <summary>
/// No legal next event exists or the lifecycle state is corrupted. Illegal
/// transitions are rejected rather than silently skipped or repaired.
/// </summary>
public sealed class AdvanceToNextEventConflictException : Exception
{
    public AdvanceToNextEventConflictException(string message)
        : base(message)
    {
    }
}
