namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One official honour for presentation on the athlete profile.
/// Read from the persisted <c>Honours</c> accelerator (no round payloads).
/// <c>LeagueKey</c> is the stable cross-season league key for league podiums;
/// null for Cup honours or when the league row is unavailable.
/// <c>Cup</c> is the route-ready Cup identifier ("color", "type" or "chaos")
/// for Cup podiums; null for league honours. Both are additive (MSS-138) and
/// absent on older payloads, which render as plain text.
/// </summary>
public sealed record AthleteHonourDto(
    int SeasonNumber,
    string LeagueName,
    string HonourKind,
    string? LeagueKey = null,
    string? Cup = null);
