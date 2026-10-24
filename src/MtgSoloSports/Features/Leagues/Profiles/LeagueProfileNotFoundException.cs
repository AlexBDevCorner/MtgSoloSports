namespace MtgSoloSports.Features.Leagues.Profiles;

/// <summary>
/// Thrown when a stable league identity has no recorded seasons in this save.
/// Mapped to HTTP 404 by the endpoint.
/// </summary>
public sealed class LeagueProfileNotFoundException : Exception
{
    public LeagueProfileNotFoundException(string message)
        : base(message)
    {
    }
}
