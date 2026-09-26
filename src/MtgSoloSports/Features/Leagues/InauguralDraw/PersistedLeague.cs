using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Leagues.InauguralDraw;

/// <summary>
/// Persisted league shape for invariant checks.
/// </summary>
public sealed record PersistedLeague(int LeagueId, SportingColor SportingColor, int Kind, string Name);
