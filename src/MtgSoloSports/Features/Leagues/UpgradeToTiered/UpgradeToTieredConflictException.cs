namespace MtgSoloSports.Features.Leagues.UpgradeToTiered;

/// <summary>
/// Thrown when a v1 save cannot enter the tiered rules yet: it is not at a
/// safe season boundary (live season in progress, pending transition without
/// rebalancing/Cup, or no pending next season). The save stays on v1 with no
/// partial upgrade. Maps to 409. Corrupted sporting state throws
/// <see cref="InvalidOperationException"/> instead.
/// </summary>
public sealed class UpgradeToTieredConflictException : Exception
{
    public UpgradeToTieredConflictException(string message)
        : base(message)
    {
    }
}
