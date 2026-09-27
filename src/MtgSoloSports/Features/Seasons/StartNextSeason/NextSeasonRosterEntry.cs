namespace MtgSoloSports.Features.Seasons.StartNextSeason;

/// <summary>
/// Validated per-league roster count for the season being started.
/// </summary>
public sealed record NextSeasonRosterEntry(
    int LeagueId,
    string LeagueName,
    string Kind,
    string SportingColor,
    int AthleteCount);
