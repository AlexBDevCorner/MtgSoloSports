namespace MtgSoloSports.Features.Cups.CompleteChaosCup;

/// <summary>
/// Thrown when a Chaos Cup bulk completion cannot run: no field exists, the
/// target stage has no persisted draw, a later stage is requested before the
/// current stage completes, or the tournament is already complete for a
/// current-stage request. Retries of already-completed stages or tournaments
/// are idempotent and return persisted progress instead of throwing; corrupted
/// sporting state throws <see cref="InvalidOperationException"/> instead.
/// Maps to 409.
/// </summary>
public sealed class CompleteChaosCupConflictException : Exception
{
    public CompleteChaosCupConflictException(string message)
        : base(message)
    {
    }
}
