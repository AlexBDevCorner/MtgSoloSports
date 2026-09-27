namespace MtgSoloSports.Features.History.ListRounds;

/// <summary>
/// Immutable presentation DTO for the round list of one historical stage.
/// Summaries never decompress payloads; the exact-round replay endpoint
/// decompresses exactly one stored payload.
/// </summary>
public sealed record ListHistoryRoundsResponse(
    Guid SaveId,
    int SeasonNumber,
    int LeagueId,
    string LeagueName,
    int StageNumber,
    int CompletedRounds,
    int RoundsPerStage,
    bool IsStageComplete,
    IReadOnlyList<HistoryRoundSummary> Rounds);
