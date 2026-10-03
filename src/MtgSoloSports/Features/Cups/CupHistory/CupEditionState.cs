namespace MtgSoloSports.Features.Cups.CupHistory;

/// <summary>
/// Where one Cup edition stands: squads selected, some rounds or events
/// played, or every event of the edition finished.
/// </summary>
public static class CupEditionState
{
    public const string Selected = "Selected";

    public const string InProgress = "InProgress";

    public const string Completed = "Completed";

    public static string For(bool anyPlayed, bool complete)
    {
        if (complete)
        {
            return Completed;
        }

        return anyPlayed ? InProgress : Selected;
    }
}
