namespace MtgSoloSports.Features.Cups.GetChaosCupEdition;

/// <summary>
/// Lightweight Chaos Cup edition overview for live/historical browsing (MSS-107).
/// Carries edition status plus one summary per group stage with group counts;
/// it never loads round payloads, so a full 1008-round tournament stays a few
/// count queries. Detailed group replay lives in <c>GetChaosCupGroup</c>.
/// Stage <see cref="StageSummary.State"/> is <c>NotDrawn</c>, <c>InProgress</c>
/// or <c>Completed</c>; edition <see cref="State"/> is <c>InProgress</c> or
/// <c>Completed</c>. An undrawn stage (including a nonexistent Final) is always
/// <c>NotDrawn</c>, never <c>Completed</c>.
/// </summary>
public sealed record GetChaosCupEditionResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int RulesVersion,
    int ChaosFormatVersion,
    int FieldSize,
    string EditionChecksum,
    string State,
    bool IsComplete,
    int StageCount,
    int TotalGroups,
    int TotalRounds,
    int RoundsPlayed,
    int GroupsCompleted,
    IReadOnlyList<GetChaosCupEditionResponse.StageSummary> Stages)
{
    public const string NotDrawn = "NotDrawn";

    public sealed record StageSummary(
        int StageIndex,
        int GroupCount,
        int EntrantCount,
        bool IsFinal,
        string State,
        bool IsDrawn,
        bool IsComplete,
        string DrawChecksum,
        int GroupsCompleted,
        int RoundsPlayed,
        int TotalRounds,
        int StandingsCount);
}
