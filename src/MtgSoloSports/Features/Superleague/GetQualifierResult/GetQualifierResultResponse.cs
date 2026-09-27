using MtgSoloSports.Features.Superleague.RunQualifier;

namespace MtgSoloSports.Features.Superleague.GetQualifierResult;

/// <summary>
/// Immutable read model for the persisted Superleague qualifier.
/// Built only from qualifier rounds/standings so replay never resimulates.
/// Without <paramref name="fromSeasonNumber"/> returns the latest resolved
/// qualifier; with it returns that season's transition.
/// </summary>
public sealed record GetQualifierResultResponse(
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
    int ChallengerQualifiedCount,
    int RoundCount);
