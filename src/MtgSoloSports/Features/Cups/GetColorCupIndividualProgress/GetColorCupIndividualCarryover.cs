namespace MtgSoloSports.Features.Cups.GetColorCupIndividualProgress;

/// <summary>
/// Round-16 to round-17 carryover continuity for one finalist (phased only).
/// QualifyingScore is the round-16 cumulative-after; Round17Before is the
/// round-17 cumulative-before; they must match for full carryover.
/// </summary>
public sealed record GetColorCupIndividualCarryover(
    int AthleteId,
    string Name,
    int QualifyingScoreThousandths,
    int Round17BeforeThousandths,
    bool Matches);
