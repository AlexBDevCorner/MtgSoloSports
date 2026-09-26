namespace MtgSoloSports.Features.Superleague.GetInauguralRoster;

/// <summary>
/// One Superleague member with its Season 1 provenance.
/// </summary>
public sealed record InauguralRosterMember(
    int AthleteId,
    string Name,
    string SportingColor,
    int FromLeagueId,
    string FromLeagueName,
    int FromSeasonRank);
