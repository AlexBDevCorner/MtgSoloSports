namespace MtgSoloSports.Features.Cups.GetColorCupTeamLive;

/// <summary>
/// Authoritative live Color Cup team projection (MSS-045). Running totals sum
/// every persisted round exactly once: all completed groups plus the rounds
/// already played in the current in-progress group. Before any round every
/// participating color team is listed with zero. While the event is incomplete
/// the table is explicitly provisional (<see cref="IsProvisional"/>): ranks are
/// a stable informational order (score descending, team name ordinal) and no
/// medals are awarded. Once complete, ranks/medals/champion/checksum are the
/// official persisted results and live totals equal them exactly. The separate
/// Color Cup individual event is unchanged.
/// </summary>
public sealed record ColorCupTeamLiveResponse(
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
    int ChampionSportingColor,
    string ChampionTeamName,
    string Checksum,
    int LastCompletedGroupNumber,
    int LastCompletedRoundNumber,
    IReadOnlyList<ColorCupTeamLiveMember> Teams);
