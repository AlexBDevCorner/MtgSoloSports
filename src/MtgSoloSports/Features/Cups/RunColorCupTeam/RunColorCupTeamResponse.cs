namespace MtgSoloSports.Features.Cups.RunColorCupTeam;

/// <summary>
/// Immutable presentation DTO for the completed Color Cup team event.
/// Built only from persisted team rounds/group standings/team standings plus
/// the champion honours so replay never resimulates. Four rank groups (all #1
/// athletes, then #2, #3, #4) each hold eight athletes over eight rounds with
/// active career bonus and normal fixed-point scoring; no new bonus is
/// generated and no league championship points are awarded. The team score is
/// the sum of the color's four legs' group scores with deterministic
/// tie-breaking. Team rank 1 holds Gold plus the official team championship
/// (one honour per winning-team member), rank 2 Silver, rank 3 Bronze.
/// </summary>
public sealed record RunColorCupTeamResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int TeamCount,
    int GroupCount,
    int GroupRounds,
    string Checksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    int ChampionSportingColor,
    string ChampionTeamName,
    IReadOnlyList<ColorCupTeamMember> Teams,
    IReadOnlyList<ColorCupTeamLegMember> Legs);
