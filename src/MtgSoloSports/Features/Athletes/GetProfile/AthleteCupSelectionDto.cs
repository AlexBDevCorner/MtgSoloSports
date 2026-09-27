namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One Cup selection for presentation on the athlete profile.
/// <c>CupKind</c> is "ColorCup" or "TypeCup"; <c>Team</c> is the sporting-color
/// name for Color Cup or the creature type for Type Cup.
/// Read from persisted Cup selection tables (no round payloads).
/// </summary>
public sealed record AthleteCupSelectionDto(
    string CupKind,
    int SourceSeasonNumber,
    string Team,
    int SelectionRank);
