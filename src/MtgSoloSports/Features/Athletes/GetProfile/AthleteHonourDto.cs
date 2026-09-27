namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One official honour for presentation on the athlete profile.
/// Read from the persisted <c>Honours</c> accelerator (no round payloads).
/// </summary>
public sealed record AthleteHonourDto(
    int SeasonNumber,
    string LeagueName,
    string HonourKind);
