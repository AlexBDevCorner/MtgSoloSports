namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Additive presentation view of the individual Color Cup qualifying cutoff.
/// Phased editions (version 1) cut 32 athletes to 16 finalists after round 16
/// with full score carryover; legacy editions (version 0) have no cutoff and
/// all fields stay null so historical 16-round Cups are never rewritten with
/// fabricated qualification rounds. Built only from persisted round payloads;
/// never resimulates and never touches league standings.
/// </summary>
public sealed record ColorCupIndividualCutoff(
    int FormatVersion,
    bool IsPhased,
    int? CutoffRank,
    int? FinalFieldSize,
    IReadOnlyList<int>? QualifyingOrderAthleteIds,
    IReadOnlyList<int>? QualifierAthleteIds,
    IReadOnlyList<int>? EliminatedAthleteIds,
    bool? TieDrawConsumed);
