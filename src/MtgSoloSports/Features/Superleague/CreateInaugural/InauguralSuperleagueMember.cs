namespace MtgSoloSports.Features.Superleague.CreateInaugural;

/// <summary>
/// One promoted athlete: identity plus its Season 1 source league and rank.
/// </summary>
public sealed record InauguralSuperleagueMember(
    int AthleteId,
    string Name,
    string SportingColor,
    int FromLeagueId,
    string FromLeagueName,
    int FromSeasonRank);
