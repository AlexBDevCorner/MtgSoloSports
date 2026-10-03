namespace MtgSoloSports.Features.Superleague.CreateInaugural;

/// <summary>
/// One promoted athlete: identity plus its Season 1 source league and rank.
/// ImageUrl carries the existing card artwork (null when unavailable) so the
/// promotion reveal renders recognizable MtgSoloSports tiles.
/// </summary>
public sealed record InauguralSuperleagueMember(
    int AthleteId,
    string Name,
    string SportingColor,
    int FromLeagueId,
    string FromLeagueName,
    int FromSeasonRank,
    string? ImageUrl = null);
