namespace MtgSoloSports.Features.Leagues.UpgradeToTiered;

/// <summary>
/// Remaining common-pool count per sporting color after seeding.
/// </summary>
public sealed record UpgradePoolCount(
    string SportingColor,
    int Count);
