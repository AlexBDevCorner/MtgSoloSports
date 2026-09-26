using MtgSoloSports.SimulationKernel.FixedPoint;
using MtgSoloSports.SimulationKernel.Random;

namespace MtgSoloSports.SimulationKernel.Scoring;

/// <summary>
/// One athlete's deterministic input for a single round simulation.
/// The caller must supply the roster in a fixed deterministic order
/// (for example name-ordinal); the simulator shuffles a copy with
/// <see cref="Pcg32V1"/> so equivalent input plus RNG reproduces the
/// equivalent finishing order. Active bonus is the bonus active at the
/// start of the stage and never includes pending bonus earned in the
/// current stage. Cumulative-before is the stage total before this round.
/// </summary>
public sealed record RoundAthleteInput(
    int AthleteId,
    string Name,
    Bonus ActiveBonus,
    Points CumulativeBefore);
