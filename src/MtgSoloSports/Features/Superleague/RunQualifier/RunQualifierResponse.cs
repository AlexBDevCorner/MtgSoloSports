namespace MtgSoloSports.Features.Superleague.RunQualifier;

/// <summary>
/// Immutable presentation DTO for the completed Superleague qualifier.
/// Built only from persisted qualifier rounds/standings so replay never
/// resimulates. The qualifier holds exactly 32 athletes (8 incumbents plus 24
/// challengers) over 16 rounds with active career bonus and normal scoring;
/// no new bonus is generated and normal league championship totals are
/// untouched. The top 8 qualify/remain in the next Superleague.
/// </summary>
public sealed record RunQualifierResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    int QualifierSize,
    int Rounds,
    int Winners,
    string Checksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    IReadOnlyList<QualifierStandingMember> Standings,
    int IncumbentQualifiedCount,
    int ChallengerQualifiedCount);
