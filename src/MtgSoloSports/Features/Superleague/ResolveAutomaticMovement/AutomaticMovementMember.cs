namespace MtgSoloSports.Features.Superleague.ResolveAutomaticMovement;

/// <summary>
/// One athlete in the postseason movement summary: identity, sporting color,
/// source league/rank provenance and resolved next-season league.
/// Built only from persisted rows so replay never resimulates.
/// ImageUrl carries the existing card artwork (null when unavailable) so the
/// promotion/relegation reveal renders recognizable MtgSoloSports tiles.
/// FromLeagueLevel/ToLeagueLevel carry the tier identity ("Superleague",
/// "Feeder1", "Feeder2", "Feeder3") derived from league rows without parsing
/// league names, so the reveal can group by boundary (MSS-060). Pool
/// sentinels (league id 0) carry null levels.
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
    string? ImageUrl = null,
    string? FromLeagueLevel = null,
    string? ToLeagueLevel = null);
