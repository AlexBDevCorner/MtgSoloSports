namespace MtgSoloSports.Features.Cups.SelectChaosCupField;

/// <summary>
/// Thrown when the Chaos Cup field cannot be selected: no completed source season is
/// ready, or the source season is not yet complete. Retries of an already-resolved
/// edition are idempotent and return the persisted field instead of throwing;
/// corrupted sporting state throws <see cref="InvalidOperationException"/> instead.
/// Maps to 409.
/// </summary>
public sealed class SelectChaosCupFieldConflictException : Exception
{
    public SelectChaosCupFieldConflictException(string message)
        : base(message)
    {
    }
}
