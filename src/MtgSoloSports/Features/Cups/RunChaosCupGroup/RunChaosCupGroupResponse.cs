namespace MtgSoloSports.Features.Cups.RunChaosCupGroup;

/// <summary>
/// Persisted-or-derived view of one 32-athlete Chaos Cup group (MSS-101).
/// Rounds are immutable facts; standings are derived from those payloads after
/// every round (partial) and persisted as <c>ChaosCupGroupStanding</c> rows
/// once the 16th round completes (final P1..P32). Replay consumes these facts
/// and never resimulates. Active bonus is the frozen edition-time effective
/// bonus; no league points, promotion or new bonus are produced.
/// </summary>
public sealed record RunChaosCupGroupResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int StageIndex,
    int GroupNumber,
    int RoundsPlayed,
    int TotalRounds,
    bool IsComplete,
    IReadOnlyList<RunChaosCupGroupResponse.RoundView> Rounds,
    IReadOnlyList<RunChaosCupGroupResponse.StandingView> Standings)
{
    public sealed record RoundView(
        int RoundNumber,
        string Checksum,
        ulong RngBeforeState,
        ulong RngBeforeStream,
        ulong RngAfterState,
        ulong RngAfterStream);

    public sealed record StandingView(
        int SaveAthleteId,
        string Name,
        int GroupRank,
        int GroupScoreThousandths,
        int BaseScoreThousandths,
        int RoundWins,
        bool IsAdvanced);
}
