namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One postseason movement for presentation on the athlete profile.
/// League id 0 is the common-pool sentinel and renders as "Common pool".
/// Read from persisted <c>Movements</c> plus season/league names.
/// </summary>
public sealed record AthleteMovementDto(
    int FromSeasonNumber,
    int ToSeasonNumber,
    string FromLeagueName,
    string ToLeagueName,
    string Kind,
    int FromSeasonRank);
