namespace MtgSoloSports.Features.Superleague.GetInauguralRoster;

/// <summary>
/// One Superleague member with its Season 1 provenance.
/// ImageUrl carries the existing card artwork (null when unavailable) so the
/// promotion reveal renders recognizable MtgSoloSports tiles.
/// </summary>
public sealed record InauguralRosterMember(
    int AthleteId,
    string Name,
    string SportingColor,
    int FromLeagueId,
    string FromLeagueName,
    int FromSeasonRank,
    string? ImageUrl = null);
