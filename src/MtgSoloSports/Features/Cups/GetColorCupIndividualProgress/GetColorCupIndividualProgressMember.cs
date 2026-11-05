namespace MtgSoloSports.Features.Cups.GetColorCupIndividualProgress;

/// <summary>
/// One athlete in the progress view. Qualifying rank/score are null for legacy
/// editions. For phased editions, qualifying rank 1..32 is the frozen round-16
/// order once 16 rounds exist (provisional top-16 before that); finalists
/// carry 100% of qualifying points.
/// </summary>
public sealed record GetColorCupIndividualProgressMember(
    int AthleteId,
    string Name,
    string SportingColor,
    int SelectionRank,
    int ProvisionalRank,
    int ScoreThousandths,
    int? QualifyingRank,
    string? AdvancingStatus,
    int? QualifyingScoreThousandths,
    int? FinalScoreThousandths,
    string? ImageUrl);
