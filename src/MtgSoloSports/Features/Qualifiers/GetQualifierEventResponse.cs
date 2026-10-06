namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Immutable read model for one qualifier event (boundary + optional color).
/// Built only from persisted rounds/standings so replay never resimulates.
/// </summary>
public sealed record GetQualifierEventResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    int BoundaryId,
    string Boundary,
    int? SportingColor,
    string SportingColorName,
    int QualifierSize,
    int RoundCount,
    int Winners,
    string Checksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    IReadOnlyList<QualifierEventMember> Standings);
