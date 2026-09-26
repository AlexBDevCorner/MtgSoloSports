using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Leagues.InauguralDraw;

/// <summary>
/// Persisted membership shape for invariant checks.
/// </summary>
public sealed record PersistedMembership(int SaveAthleteId, SportingColor SportingColor, int? LeagueId, int DrawIndex);
