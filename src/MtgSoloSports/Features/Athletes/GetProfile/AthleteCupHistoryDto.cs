namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One completed Cup appearance for presentation on the athlete profile.
/// Unified across individual and team events so League history and Cup
/// history feel like parts of the same profile: season, place and result
/// plus enough event/team metadata to keep rows unambiguous.
/// <c>Cup</c> is "Color" or "Type" (matches the Cup history slices);
/// <c>Event</c> is "Individual" or "Team";
/// <c>EventName</c> is the human-readable event ("Color Cup",
/// "Color Cup Team" or "Type Cup Team").
/// <c>TeamKey</c>/<c>TeamName</c> identify the represented side: the
/// lower-case sporting-color key and color name for Color Cup entries, or
/// the creature type for Type Cup entries. For team events <c>Place</c> is
/// the team's final rank and <c>Medal</c> the team's medal; for individual
/// events <c>Place</c> is the athlete's own Cup rank and <c>Medal</c> the
/// athlete's own medal. <c>ScoreThousandths</c> is the matching final score
/// (individual Cup score or team score, fixed-point thousandths).
/// <c>GroupRank</c>/<c>GroupNumber</c> carry the athlete's own leg context
/// for team events and are null for individual events.
/// <c>TournamentPhase</c> is Type Cup only (0 legacy single-field, 1
/// qualification, 2 Final; null for Color Cup entries).
/// <c>QualificationGroup</c> is the persisted qualification group (1..G) for
/// qualification-stage Type Cup legs and null otherwise.
/// <c>TournamentStage</c> is a data-driven stage label for Type Cup team
/// entries ("Final", "Qualification Group A", ...; "Single field" for legacy
/// rows) and null for Color Cup entries, so Final participation is
/// distinguishable from qualification-only participation without parsing
/// names. Qualification rows never carry official medals/honours; only Final
/// ranks 1-3 hold them.
/// Read from persisted Cup standings only (no round payloads, no current
/// membership); historical rows stay correct even if the athlete later
/// changes team or becomes inactive.
/// </summary>
public sealed record AthleteCupHistoryDto(
    int SourceSeasonNumber,
    string Cup,
    string Event,
    string EventName,
    string TeamKey,
    string TeamName,
    int Place,
    string Medal,
    int ScoreThousandths,
    int? GroupRank,
    int? GroupNumber,
    int? TournamentPhase = null,
    int? QualificationGroup = null,
    string? TournamentStage = null);
