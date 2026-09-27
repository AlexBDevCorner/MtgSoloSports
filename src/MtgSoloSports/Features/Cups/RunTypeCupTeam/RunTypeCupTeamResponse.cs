namespace MtgSoloSports.Features.Cups.RunTypeCupTeam;

/// <summary>
/// Immutable presentation DTO for the completed Type Cup team event.
/// Built only from persisted team rounds/group standings/team standings plus
/// the champion honours so replay never resimulates. Four rank groups (all #1
/// athletes, then #2, #3, #4) each hold N athletes (one per participating
/// creature type at the same selection rank, where N is the dynamically varying
/// team count) over eight rounds with active career bonus and normal fixed-point
/// scoring; no new bonus is generated and no league championship points are
/// awarded. The team score is the sum of the type's four legs' group scores with
/// deterministic tie-breaking. Team rank 1 holds Gold plus the official Type Cup
/// team championship (one honour per winning-team member), rank 2 Silver, rank 3
/// Bronze. Uncapped participants are capped to their creature type atomically
/// with the event; capped athletes keep their permanent nationality.
/// </summary>
public sealed record RunTypeCupTeamResponse(
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
    string ChampionCreatureType,
    string ChampionTeamName,
    IReadOnlyList<TypeCupTeamMember> Teams,
    IReadOnlyList<TypeCupTeamLegMember> Legs);
