namespace MtgSoloSports.Features.Cups.GetTypeCupTeamResult;

/// <summary>
/// Immutable read model for the persisted Type Cup team event.
/// Built only from team rounds/group standings/team standings plus the
/// champion honours so replay never resimulates. Without
/// <paramref name="sourceSeasonNumber"/> returns the latest resolved team
/// event; with it returns that source season's event. The team count N varies
/// per source season; only the Color Cup has a fixed eight-team field.
/// </summary>
public sealed record GetTypeCupTeamResultResponse(
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
    IReadOnlyList<GetTypeCupTeamMember> Teams,
    IReadOnlyList<GetTypeCupTeamLegMember> Legs);
