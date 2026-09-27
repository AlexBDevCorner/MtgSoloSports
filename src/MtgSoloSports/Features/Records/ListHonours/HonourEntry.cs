namespace MtgSoloSports.Features.Records.ListHonours;

/// <summary>
/// One official honour (league championship) for history and Hall of Fame views.
/// </summary>
public sealed record HonourEntry(
    int SeasonNumber,
    int SeasonId,
    int LeagueId,
    string LeagueName,
    int LeagueKind,
    string HonourKind,
    int AthleteId,
    string AthleteName);
