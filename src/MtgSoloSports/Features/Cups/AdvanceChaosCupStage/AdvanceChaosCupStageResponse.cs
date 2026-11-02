namespace MtgSoloSports.Features.Cups.AdvanceChaosCupStage;

/// <summary>
/// Persisted next-stage Chaos Cup draw for one completed source season (MSS-102).
/// Advancing requires every group of the previous stage to be complete (16 rounds
/// plus P1..P32 standings); exactly the top 16 per group qualify and group scores
/// reset. Stages 1..5 hold 16/8/4/2/1 fresh 32-athlete groups drawn pure random
/// with no color or tier seeding. The Final (single group) holds the official
/// P1..P32 once played; eliminated athletes carry only their exit-stage category
/// with no fabricated global ranks 33..1024. Built only from persisted edition,
/// entrant, standing and draw rows so replay never resimulates.
/// </summary>
public sealed record AdvanceChaosCupStageResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int RulesVersion,
    int ChaosFormatVersion,
    int PrevStageIndex,
    int StageIndex,
    int GroupCount,
    int EntrantCount,
    int AdvancingFromPrev,
    bool IsFinal,
    string EditionChecksum,
    string DrawChecksum,
    ulong RngBeforeState,
    ulong RngBeforeStream,
    ulong RngAfterState,
    ulong RngAfterStream,
    IReadOnlyList<AdvanceChaosCupStageResponse.DrawGroup> Groups)
{
    public sealed record DrawGroup(
        int GroupNumber,
        IReadOnlyList<int> AthleteIds);
}
