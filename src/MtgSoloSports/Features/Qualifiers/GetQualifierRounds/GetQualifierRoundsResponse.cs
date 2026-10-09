namespace MtgSoloSports.Features.Qualifiers.GetQualifierRounds;

/// <summary>
/// Immutable read model for one feeder qualifier event in any persisted state
/// (pending, in-progress, complete). Field comes from the source standings;
/// rounds/standings come from persisted qualifier rows only, so replay never
/// resimulates and GETs never consume RNG. Each event shows its own
/// 0..16 / 16 progression; phase-wide 17-event / 272-round totals live on the
/// qualifier overview, never under this individual event title.
/// </summary>
public sealed record GetQualifierRoundsResponse(
    Guid SaveId,
    int FromSeasonNumber,
    int ToSeasonNumber,
    string Boundary,
    int BoundaryId,
    int SportingColor,
    string SportingColorName,
    int RoundsPlayed,
    int TotalRounds,
    bool IsComplete,
    string Checksum,
    IReadOnlyList<QualifierRoundSummary> Rounds,
    IReadOnlyList<QualifierFieldMember> Field,
    IReadOnlyList<QualifierEventMember> Standings);
