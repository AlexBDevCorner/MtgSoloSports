using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Seasons.SeasonLifecycle;

/// <summary>
/// Post-rebalance Cup extension point. Game rules run one Cup after every
/// season (odd seasons Color Cup, even seasons Type Cup) between feeder
/// rebalancing and season finalization/next-season start. Cups are integrated
/// by later slices (MSS-020/021); this helper reserves the ordering explicitly
/// so MSS-018 leaves a clear hook without implementing Cup simulation.
/// </summary>
public static class CupExtensionPoint
{
    public const string NoCup = "None";
    public const string ColorCup = "ColorCup";
    public const string TypeCup = "TypeCup";

    /// <summary>
    /// Returns the Cup that will run for a completed source season once Cups
    /// are implemented. Before rebalancing the Cup is not yet runnable.
    /// </summary>
    public static string ExpectedCupForSource(int sourceSeasonNumber)
    {
        if (sourceSeasonNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSeasonNumber));
        }

        return sourceSeasonNumber % 2 == 1 ? ColorCup : TypeCup;
    }

    /// <summary>
    /// Validates the Cup extension point. Cups currently persist no tables, so
    /// this is a no-op that documents where Color/Type Cup execution will be
    /// inserted: after <c>Rebalanced</c> and before <c>StartNextSeason</c>.
    /// Future Cup slices will persist Cup results here and StartNextSeason will
    /// require them. Never consumes sporting RNG.
    /// </summary>
    public static void ValidateCupExtensionPoint(SaveDbContext context, SeasonEntity source, SeasonEntity next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(next);
    }
}
