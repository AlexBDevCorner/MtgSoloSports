namespace MtgSoloSports.Features.Leagues.GetSeason1Leagues;

/// <summary>
/// Remaining common-pool counts per sporting color.
/// </summary>
public sealed record Season1PoolCount(
    string SportingColor,
    int Count);
