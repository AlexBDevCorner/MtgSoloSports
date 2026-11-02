namespace MtgSoloSports.Features.Cups.AdvanceChaosCupStage;

/// <summary>
/// Thrown when the next Chaos Cup stage cannot be drawn: no field exists, the
/// previous stage is not yet fully complete, or the tournament is already
/// complete. Retries of an already-drawn stage are idempotent and return the
/// persisted draw instead of throwing; corrupted sporting state throws
/// <see cref="InvalidOperationException"/> instead.
/// Maps to 409.
/// </summary>
public sealed class AdvanceChaosCupStageConflictException : Exception
{
    public AdvanceChaosCupStageConflictException(string message)
        : base(message)
    {
    }
}
