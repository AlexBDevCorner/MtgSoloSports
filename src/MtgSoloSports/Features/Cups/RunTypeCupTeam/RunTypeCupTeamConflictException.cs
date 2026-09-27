namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Conflict marker when the Type Cup team event cannot run: no completed even
/// season with a resolved allocation, fewer than two participating creature-type
/// teams, or the team event has already been resolved for the source season.
/// Mapped to HTTP 409.
/// </summary>
public sealed class RunTypeCupTeamConflictException : Exception
{
    public RunTypeCupTeamConflictException(string message)
        : base(message)
    {
    }
}
