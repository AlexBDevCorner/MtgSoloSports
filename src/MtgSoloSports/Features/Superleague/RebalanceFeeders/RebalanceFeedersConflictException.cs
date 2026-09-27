namespace MtgSoloSports.Features.Superleague.RebalanceFeeders;

/// <summary>
/// Thrown when feeder rebalancing cannot run: no pending postseason transition,
/// movement/qualifier outcomes unresolved, or rebalancing already completed.
/// Maps to 409. Corrupted sporting state throws
/// <see cref="InvalidOperationException"/> instead.
/// </summary>
public sealed class RebalanceFeedersConflictException : Exception
{
    public RebalanceFeedersConflictException(string message)
        : base(message)
    {
    }
}
