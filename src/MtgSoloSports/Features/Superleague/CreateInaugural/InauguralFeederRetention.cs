namespace MtgSoloSports.Features.Superleague.CreateInaugural;

/// <summary>
/// Retained feeder count per Season 2 league (28 pending rebalancing).
/// </summary>
public sealed record InauguralFeederRetention(
    int LeagueId,
    string LeagueName,
    string SportingColor,
    int RetainedCount);
