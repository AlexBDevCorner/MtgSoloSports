namespace MtgSoloSports.Features.Simulation.AdvanceRound;

/// <summary>
/// One athlete's immutable presentation row within an advanced round.
/// All sporting values are fixed-point thousandths; whole-point projections
/// are derived, never recomputed with floating point.
/// </summary>
public sealed record AdvanceRoundPlacement(
    int AthleteId,
    string Name,
    int Position,
    int BaseThousandths,
    int ActiveBonusThousandths,
    int FinalThousandths,
    int CumulativeBeforeThousandths,
    int CumulativeAfterThousandths,
    int RankBefore,
    int RankAfter,
    int RankMovement);
