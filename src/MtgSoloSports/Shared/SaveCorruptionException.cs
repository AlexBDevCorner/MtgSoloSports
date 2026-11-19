namespace MtgSoloSports.Shared;

/// <summary>
/// A save's persisted sporting state is corrupt, or a handler/persistence
/// invariant over persisted rows failed. This is a server failure: it maps to
/// HTTP 500 with the corruption message, never to 400/409, and no caller may
/// swallow or downgrade it. Deliberately does not derive from
/// <see cref="InvalidOperationException"/> so an un-audited
/// <c>catch (InvalidOperationException)</c> filter can never silently absorb a
/// corruption signal. Never use it for argument validation.
/// </summary>
public class SaveCorruptionException : Exception
{
    public SaveCorruptionException(string message)
        : base(message)
    {
    }

    public SaveCorruptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
