namespace MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;

/// <summary>
/// One athlete in the postseason movement summary: identity, sporting color,
/// source league/rank provenance and resolved next-season league.
/// Built only from persisted rows so replay never resimulates.
/// ImageUrl carries the existing card artwork (null when unavailable) so the
/// promotion/relegation reveal renders recognizable MtgSoloSports tiles.
/// </summary>
public sealed record AutomaticMovementMember(
    int AthleteId,
    string Name,
    string SportingColor,
    int FromLeagueId,
    string FromLeagueName,
    int FromSeasonRank,
    int ToLeagueId,
    string ToLeagueName,
    string MovementKind,
    string? ImageUrl = null);
