namespace MtgSoloSports.Features.Superleague.GetRebalanceResult;

/// <summary>
/// Thrown when no feeder rebalancing has been resolved yet. Maps to 404.
/// Corrupted sporting state throws <see cref="MtgSoloSports.Shared.SaveCorruptionException"/> instead (MSS-144: 500).
/// </summary>
public sealed class RebalanceResultNotFoundException : Exception
{
    public RebalanceResultNotFoundException(string message)
        : base(message)
    {
    }
}
