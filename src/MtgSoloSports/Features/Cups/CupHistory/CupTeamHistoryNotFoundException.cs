namespace MtgSoloSports.Features.Cups.CupHistory;

/// <summary>
/// Thrown when a Cup team key names no team that was ever selected. Maps to 404.
/// </summary>
public sealed class CupTeamHistoryNotFoundException : Exception
{
    public CupTeamHistoryNotFoundException(string message)
        : base(message)
    {
    }
}
