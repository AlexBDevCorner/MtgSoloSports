namespace MtgSoloSports.Features.Qualifiers;

/// <summary>
/// Immutable presentation DTO for one completed feeder qualifier event.
/// Built only from persisted qualifier rounds/standings so replay never
/// resimulates. Each event holds exactly 16 athletes (8 incumbents + 8
/// challengers) over 16 rounds; top 8 occupy/remain in the higher tier.
/// </summary>
public sealed record RunFeederQualifierResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    string BoundaryId,
    string Boundary,
    int SportingColor,
    string SportingColorName,
    int QualifierSize,
    int Rounds,
    int Winners,
    string Checksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    IReadOnlyList<FeederQualifierStandingMember> Standings);
