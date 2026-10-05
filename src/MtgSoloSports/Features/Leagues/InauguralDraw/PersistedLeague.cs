using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Leagues.InauguralDraw;

/// <summary>
/// Persisted league shape for invariant checks.
/// Feeder division is explicit; Season 1 feeders must be Feeder 1.
/// </summary>
public sealed record PersistedLeague(int LeagueId, SportingColor SportingColor, int Kind, int FeederDivision, string Name);
