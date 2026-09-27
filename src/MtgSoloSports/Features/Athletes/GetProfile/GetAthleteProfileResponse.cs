namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Immutable athlete profile response: identity plus career plus
/// season-by-season history ordered by season number, plus official honours,
/// postseason movements and Cup selections for the spectator profile view.
/// Honours/movements/selections are read from normalized tables only and never
/// decompress round payloads. Empty lists mean "no honours yet", not missing data.
/// </summary>
public sealed record GetAthleteProfileResponse(
    Guid SaveId,
    int AthleteId,
    AthleteCardDto Card,
    AthleteCareerDto Career,
    IReadOnlyList<AthleteSeasonDto> Seasons,
    IReadOnlyList<AthleteHonourDto> Honours,
    IReadOnlyList<AthleteMovementDto> Movements,
    IReadOnlyList<AthleteCupSelectionDto> CupSelections);
