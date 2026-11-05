namespace MtgSoloSports.Features.Cups.GetColorCupIndividualProgress;

/// <summary>
/// Format-aware read model for the individual Color Cup while it is played
/// and after it completes. Built only from persisted Cup rounds/standings;
/// never resimulates and never touches league standings. Legacy editions have
/// no cutoff (all cutoff fields null); phased editions expose the round-16
/// top-16 boundary, qualified/eliminated state, cumulative scores and
/// round-16 to round-17 carryover continuity. Provisional ranks before
/// completion are display-only and never consume RNG.
/// </summary>
public sealed record GetColorCupIndividualProgressResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int FormatVersion,
    bool IsPhased,
    int TotalRounds,
    int RoundsPlayed,
    bool IsComplete,
    int QualifyingRounds,
    int? CutoffRank,
    int? FinalFieldSize,
    IReadOnlyList<int>? QualifyingOrderAthleteIds,
    IReadOnlyList<int>? QualifierAthleteIds,
    IReadOnlyList<int>? EliminatedAthleteIds,
    bool? TieDrawConsumed,
    bool IsProvisionalCutoff,
    IReadOnlyList<GetColorCupIndividualProgressMember> Standings,
    IReadOnlyList<GetColorCupIndividualCarryover>? Carryover);
