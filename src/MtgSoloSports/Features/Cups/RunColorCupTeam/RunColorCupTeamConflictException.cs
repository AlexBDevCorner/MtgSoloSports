namespace MtgSoloSports.Features.Cups.RunColorCupTeam;

/// <summary>
/// Conflict marker when the Color Cup team event cannot run: no completed odd
/// season with a resolved 32-athlete selection, or the team event has already
/// been resolved for the source season. Mapped to HTTP 409.
/// </summary>
public sealed class RunColorCupTeamConflictException : Exception
{
    public RunColorCupTeamConflictException(string message)
        : base(message)
    {
    }
}
