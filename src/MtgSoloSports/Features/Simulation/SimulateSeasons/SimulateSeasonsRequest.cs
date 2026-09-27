namespace MtgSoloSports.Features.Simulation.SimulateSeasons;

/// <summary>
/// Explicit multi-season simulation request. <see cref="Seasons"/> counts
/// completed season transitions (<c>StartNextSeason</c> successes): starting
/// mid-season N, <c>Seasons = 1</c> finishes season N plus its postseason
/// chain and starts season N+1. The upper bound preserves hundreds-of-seasons
/// capability via repeated calls while preventing accidental huge HTTP
/// operations; thousands of seasons remain possible across calls.
/// </summary>
public sealed record SimulateSeasonsRequest(int Seasons);
