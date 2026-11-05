namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Immutable presentation DTO for the completed Color Cup individual event.
/// Built only from persisted Cup rounds/standings plus the champion honour so
/// replay never resimulates. Legacy editions hold 32 athletes over 16 rounds;
/// phased editions (MSS-120) hold 16 qualifying rounds of 32 plus 16 final
/// rounds of the top-16 qualifiers with 100% carryover. Active career bonus
/// applies with normal scoring; no new bonus is generated and no league
/// championship points are awarded. Rank 1 holds Gold plus the official
/// individual championship, rank 2 Silver, rank 3 Bronze.
/// Cutoff fields are additive and null for legacy editions.
/// </summary>
public sealed record RunColorCupIndividualResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int CupSize,
    int Rounds,
    string Checksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    int ChampionAthleteId,
    string ChampionName,
    IReadOnlyList<ColorCupIndividualMember> Standings,
    int FormatVersion = 0,
    bool IsPhased = false,
    int QualifyingRounds = 16,
    int? CutoffRank = null,
    int? FinalFieldSize = null,
    IReadOnlyList<int>? QualifyingOrderAthleteIds = null,
    IReadOnlyList<int>? QualifierAthleteIds = null,
    IReadOnlyList<int>? EliminatedAthleteIds = null,
    bool? TieDrawConsumed = null);
