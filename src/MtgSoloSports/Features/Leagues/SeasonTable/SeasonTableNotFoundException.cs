namespace MtgSoloSports.Features.Leagues.SeasonTable;

/// <summary>
/// Thrown when a completed season table does not exist yet (season not
/// finalized). Mapped to HTTP 404 by the endpoint.
/// </summary>
public sealed class SeasonTableNotFoundException : Exception
{
    public SeasonTableNotFoundException(string message)
        : base(message)
    {
    }
}
