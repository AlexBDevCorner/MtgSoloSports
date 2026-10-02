namespace MtgSoloSports.Features.Cups.GetTypeCupTeamLive;

/// <summary>
/// Authoritative live Type Cup team projection (MSS-045). Running totals sum
/// every persisted round exactly once: all completed groups plus the rounds
/// already played in the current in-progress group. Before any round every
/// participating team is listed with zero. While the event is incomplete the
/// table is explicitly provisional (<see cref="IsProvisional"/>): ranks are a
/// stable informational order (score descending, team name ordinal) and no
/// medals are awarded. Once complete, ranks/medals/champion/checksum are the
/// official persisted results and live totals equal them exactly.
/// </summary>
public sealed record TypeCupTeamLiveResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int TeamCount,
    int GroupCount,
    int GroupRounds,
    int CompletedRounds,
    int TotalRounds,
    int CurrentGroupNumber,
    int CurrentRoundNumber,
    bool IsComplete,
    bool IsProvisional,
    string ChampionCreatureType,
    string Checksum,
    int LastCompletedGroupNumber,
    int LastCompletedRoundNumber,
    IReadOnlyList<TypeCupTeamLiveMember> Teams);
