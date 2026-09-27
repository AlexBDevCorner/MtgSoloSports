namespace MtgSoloSports.Features.History;

/// <summary>
/// Thrown when a historical season, competition, stage or round does not exist
/// yet. Mapped to 404 so callers can distinguish "not yet simulated" from bad
/// requests. Never thrown for corrupt state; corruption aborts with
/// <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class HistoryNotFoundException : Exception
{
    public HistoryNotFoundException(string message)
        : base(message)
    {
    }
}
