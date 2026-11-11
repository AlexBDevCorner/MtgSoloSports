namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One postseason movement for presentation on the athlete profile.
/// League id 0 is the common-pool sentinel and renders as "Common pool".
/// Read from persisted <c>Movements</c> plus season/league names.
/// FromLeagueLevel/ToLeagueLevel carry the adjacent-tier source/destination
/// ("Superleague", "Feeder1", "Feeder2", "Feeder3") derived from league rows
/// without parsing league names (MSS-060); null for the pool sentinel. The
/// <c>Kind</c> string already distinguishes automatic, qualifier-based and
/// structural rebalance movement.
/// FromLeagueKey/ToLeagueKey carry the stable cross-season league keys
/// (<c>LeagueKey</c>); null for the pool sentinel or when the league row is
/// unavailable. Additive (MSS-138): absent on older payloads.
/// </summary>
public sealed record AthleteMovementDto(
    int FromSeasonNumber,
    int ToSeasonNumber,
    string FromLeagueName,
    string ToLeagueName,
    string Kind,
    int FromSeasonRank,
    string? FromLeagueLevel = null,
    string? ToLeagueLevel = null,
    string? FromLeagueKey = null,
    string? ToLeagueKey = null);
