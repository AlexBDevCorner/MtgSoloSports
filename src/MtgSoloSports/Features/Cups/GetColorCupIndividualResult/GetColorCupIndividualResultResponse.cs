namespace MtgSoloSports.Features.Cups.GetColorCupIndividualResult;

/// <summary>
/// Immutable read model for the persisted Color Cup individual event.
/// Built only from Cup rounds/standings plus the champion honour so replay
/// never resimulates. Without <paramref name="sourceSeasonNumber"/> returns
/// the latest resolved Cup; with it returns that source season's Cup.
/// </summary>
public sealed record GetColorCupIndividualResultResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int CupSize,
    int Rounds,
    string Checksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    int ChampionAthleteId,
    string ChampionName,
    IReadOnlyList<GetColorCupIndividualMember> Standings);
