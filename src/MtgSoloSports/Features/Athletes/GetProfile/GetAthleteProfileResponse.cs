namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// Immutable athlete profile response: identity plus career plus
/// season-by-season history ordered by season number, plus official honours,
/// postseason movements, Cup selections and Cup history for the spectator
/// profile view.
/// Honours/movements/selections/history are read from normalized tables only
/// and never decompress round payloads. Empty lists mean "none yet", not
/// missing data. Cup history is newest season first (each Cup/event is its own
/// row) while League history stays oldest first. Chaos history is one entry
/// per participated Chaos edition (newest first) with the frozen source-tier
/// or Pool attribution plus the complete group trajectory for linking to
/// persisted history; absent editions have no entry and incomplete editions
/// stay provisional.
/// </summary>
public sealed record GetAthleteProfileResponse(
    Guid SaveId,
    int AthleteId,
    AthleteCardDto Card,
    AthleteCareerDto Career,
    IReadOnlyList<AthleteSeasonDto> Seasons,
    IReadOnlyList<AthleteHonourDto> Honours,
    IReadOnlyList<AthleteMovementDto> Movements,
    IReadOnlyList<AthleteCupSelectionDto> CupSelections,
    IReadOnlyList<AthleteCupHistoryDto> CupHistory,
    IReadOnlyList<AthleteChaosHistoryDto> ChaosHistory);
