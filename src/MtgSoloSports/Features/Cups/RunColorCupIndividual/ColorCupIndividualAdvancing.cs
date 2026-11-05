namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Per-athlete advancing state for a completed individual Cup. Null for legacy
/// editions. For phased editions, qualifying rank 1..32 is the frozen
/// round-16 order; finalists carry 100% of qualifying points into the final.
/// </summary>
public sealed record ColorCupIndividualAdvancing(
    int? QualifyingRank,
    bool? QualifiedForFinal,
    int? QualifyingScoreThousandths,
    int? FinalScoreThousandths);
