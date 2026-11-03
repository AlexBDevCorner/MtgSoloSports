namespace MtgSoloSports.Features.Athletes.GetProfile;

/// <summary>
/// One Chaos Cup edition on an athlete's profile (MSS-113). Individual
/// 1024-entrant tournament; the athlete holds one entry per participated
/// edition with the attained stage, Final place when applicable, medal and
/// the complete group trajectory for linking to persisted history. Source
/// attribution is the frozen edition-time tier or Pool from the entrant row,
/// never the athlete's current league, so a former Pool champion keeps the
/// historic title after promotion and Pool invites never gain nationality or
/// team eligibility. Only Final ranks 1..3 carry medals; every other finish
/// (including group wins) persists <c>None</c>. Absent editions have no entry;
/// drawn but incomplete editions stay provisional with no fabricated ranks.
/// </summary>
public sealed record AthleteChaosHistoryDto(
    int SourceSeasonNumber,
    bool IsPoolInvite,
    string SourceLabel,
    int? SourceLeagueId,
    int SourceLeagueKind,
    int SourceFeederDivision,
    int StagesEntered,
    bool IsFinalist,
    bool IsEliminated,
    int? EliminatedStageIndex,
    string? ExitStage,
    int? FinalPlace,
    string Medal,
    bool IsComplete,
    IReadOnlyList<AthleteChaosStageDto> Stages);
