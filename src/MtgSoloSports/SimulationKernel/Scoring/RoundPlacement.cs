using MtgSoloSports.SimulationKernel.FixedPoint;

namespace MtgSoloSports.SimulationKernel.Scoring;

/// <summary>
/// One athlete's immutable placement within a simulated round, in finishing order.
/// Cumulative-after equals cumulative-before plus final points; ranks are
/// deterministic stage ranks before/after this round.
/// </summary>
public sealed record RoundPlacement(
    int AthleteId,
    string Name,
    int Position,
    Points BasePoints,
    Bonus ActiveBonus,
    Points FinalPoints,
    Points CumulativeBefore,
    Points CumulativeAfter,
    int RankBefore,
    int RankAfter,
    int RankMovement);
