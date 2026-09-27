namespace MtgSoloSports.Features.Cups.GetColorCupTeamResult;

/// <summary>
/// Immutable read model for the persisted Color Cup team event.
/// Built only from team rounds/group standings/team standings plus the
/// champion honours so replay never resimulates. Without
/// <paramref name="sourceSeasonNumber"/> returns the latest resolved team
/// event; with it returns that source season's event.
/// </summary>
public sealed record GetColorCupTeamResultResponse(
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
    IReadOnlyList<GetColorCupTeamMember> Teams,
    IReadOnlyList<GetColorCupTeamLegMember> Legs);
