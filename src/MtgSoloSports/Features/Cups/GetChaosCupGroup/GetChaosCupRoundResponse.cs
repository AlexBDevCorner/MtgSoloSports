namespace MtgSoloSports.Features.Cups.GetChaosCupGroup;

/// <summary>
/// Immutable replay of one persisted Chaos Cup game round (MSS-107). Built only
/// from the stored compact payload; reads never resimulate. A round that has
/// not been played yet 404s and is never portrayed as completed.
/// </summary>
public sealed record GetChaosCupRoundResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int StageIndex,
    int GroupNumber,
    int RoundNumber,
    int RulesVersion,
    int ChaosFormatVersion,
    string Checksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    IReadOnlyList<GetChaosCupRoundResponse.Placement> Placements)
{
    public sealed record Placement(
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
        string? ImageUrl);
}
