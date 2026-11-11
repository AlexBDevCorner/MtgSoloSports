namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One Cup selection for presentation on the athlete profile.
/// <c>CupKind</c> is "ColorCup" or "TypeCup"; <c>Team</c> is the sporting-color
/// name for Color Cup or the creature type for Type Cup.
/// Read from persisted Cup selection tables (no round payloads).
/// <c>Cup</c> is the route-ready Cup identifier ("color" or "type") and
/// <c>TeamKey</c> the stable team key (lower-case color name for Color Cup,
/// creature type as stored for Type Cup) so the profile links without parsing
/// display strings. Both are additive (MSS-138) and absent on older payloads.
/// </summary>
public sealed record AthleteCupSelectionDto(
    string CupKind,
    int SourceSeasonNumber,
    string Team,
    int SelectionRank,
    string? Cup = null,
    string? TeamKey = null);
