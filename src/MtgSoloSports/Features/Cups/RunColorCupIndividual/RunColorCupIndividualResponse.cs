namespace MtgSoloSports.Features.Cups.RunColorCupIndividual;

/// <summary>
/// Immutable presentation DTO for the completed Color Cup individual event.
/// Built only from persisted Cup rounds/standings plus the champion honour so
/// replay never resimulates. The Cup holds exactly the 32 selected athletes
/// over 16 rounds with active career bonus and normal scoring; no new bonus
/// is generated and no league championship points are awarded. Rank 1 holds
/// Gold plus the official individual championship, rank 2 Silver, rank 3 Bronze.
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
    IReadOnlyList<ColorCupIndividualMember> Standings);
