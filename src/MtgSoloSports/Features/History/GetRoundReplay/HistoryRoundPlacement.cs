namespace MtgSoloSports.Features.History.GetRoundReplay;

/// <summary>
/// One athlete's immutable presentation row inside a historical replay.
/// Field-for-field compatible with the live round presentation model
/// (<c>GetStageRoundPlacement</c>), so an old round feeds the same React table
/// without resimulation. All sporting values are fixed-point thousandths copied
/// verbatim from the persisted payload; card art fields are display-only.
/// </summary>
public sealed record HistoryRoundPlacement(
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
