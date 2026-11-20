using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Qualifiers.Engine;

/// <summary>
/// One athlete's provenance inside a qualifier field (MSS-150): the single
/// normalized pick shape consumed by the shared qualifier engine. Both
/// pipelines map their own field-selection picks onto this shape; role is
/// provenance only and simulation treats incumbents and challengers
/// identically.
/// </summary>
internal sealed record QualifierProvenance(
    int SaveAthleteId,
    string Name,
    int SportingColor,
    int FromLeagueId,
    int FromSeasonRank,
    QualifierRole Role);
