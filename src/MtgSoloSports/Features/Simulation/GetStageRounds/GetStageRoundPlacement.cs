namespace MtgSoloSports.Features.Simulation.GetStageRounds;

/// <summary>
/// One athlete's immutable presentation row inside a persisted stage round.
/// All sporting values are fixed-point thousandths copied verbatim from the
/// persisted round payload; card art fields are display-only references from
/// the save-owned athlete snapshot and never affect sporting outcomes.
/// </summary>
public sealed record GetStageRoundPlacement(
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
    int RankMovement,
    string? ImageUrl,
    string? SetCode,
    string TypeLine);
