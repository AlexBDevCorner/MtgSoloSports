namespace MtgSoloSports.Features.Superleague.GetFeederMovements;

/// <summary>
/// One athlete in the persisted feeder automatic-movement summary (MSS-060).
/// Covers the two feeder boundaries (F1↔F2, F2↔F3) that the Superleague-scoped
/// automatic-movement read model omits: automatic promotions/relegations plus
/// qualifier incumbents/challengers with source rank provenance, adjacent-tier
/// source/destination identity and next-league assignment. Built only from
/// persisted rows so replay never resimulates.
/// </summary>
public sealed record FeederMovementMember(
    int AthleteId,
    string Name,
    int SportingColor,
    string SportingColorName,
    int BoundaryId,
    string Boundary,
    int FromLeagueId,
    string FromLeagueName,
    string FromLeagueLevel,
    int FromSeasonRank,
    int ToLeagueId,
    string ToLeagueName,
    string? ToLeagueLevel,
    string MovementKind,
    string? ImageUrl = null);
