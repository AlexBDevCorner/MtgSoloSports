using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Leagues.InauguralDraw;

/// <summary>
/// One athlete's inaugural-draw outcome within its sporting color's 256-athlete
/// save population. <see cref="DrawIndex"/> is the position in the shuffled
/// order (0..255); the first <c>LeagueSize</c> entries are league members in
/// draw order, the rest remain in the common pool.
/// </summary>
public sealed record InauguralDrawEntry(
    string Name,
    SportingColor SportingColor,
    int DrawIndex,
    bool IsLeagueMember);
