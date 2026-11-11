namespace MtgSoloSports.Features.Records.ListHonours;

/// <summary>
/// One official honour (league championship) for history and Hall of Fame views.
/// <c>LeagueKey</c> is the stable cross-season league identity for league
/// honours (MSS-139 additive, null for Cup honours and legacy rows); text
/// without a key renders as plain text and never a guessed URL.
/// </summary>
public sealed record HonourEntry(
    int SeasonNumber,
    int SeasonId,
    int LeagueId,
    string LeagueName,
    int LeagueKind,
    string HonourKind,
    int AthleteId,
    string AthleteName,
    string? LeagueKey = null);
