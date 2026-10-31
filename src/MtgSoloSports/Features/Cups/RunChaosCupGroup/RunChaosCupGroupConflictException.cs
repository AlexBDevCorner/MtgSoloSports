namespace MtgSoloSports.Features.Cups.RunChaosCupGroup;

/// <summary>
/// Thrown when one Chaos Cup group cannot advance: the field is missing, the
/// stage/group is out of range or undrawn, or the group is already complete
/// for play-next. Retries of an already-completed group via finish are
/// idempotent and return the persisted result instead of throwing; corrupted
/// sporting state throws <see cref="InvalidOperationException"/> instead.
/// Maps to 409.
/// </summary>
public sealed class RunChaosCupGroupConflictException : Exception
{
    public RunChaosCupGroupConflictException(string message)
        : base(message)
    {
    }
}
