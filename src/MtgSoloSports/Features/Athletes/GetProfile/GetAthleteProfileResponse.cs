namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Immutable athlete profile response: identity plus career plus
/// season-by-season history ordered by season number.
/// </summary>
public sealed record GetAthleteProfileResponse(
    Guid SaveId,
    int AthleteId,
    AthleteCardDto Card,
    AthleteCareerDto Career,
    IReadOnlyList<AthleteSeasonDto> Seasons);
