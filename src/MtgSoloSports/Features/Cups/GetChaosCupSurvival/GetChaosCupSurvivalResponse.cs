namespace MtgSoloSports.Features.Cups.GetChaosCupSurvival;

/// <summary>
/// Read-only Chaos Cup color/division survival analytics (MSS-108). Summarizes
/// each group stage's initial, advancing, eliminated, pending and Finalist
/// athletes from persisted draws and official group standings, broken down by
/// the eight permanent sporting colors and by frozen source tier (Pool,
/// Superleague, Feeder 1/2/3). Per-stage color and division counts sum exactly
/// to the stage totals. A drawn but incomplete stage stays provisional; an
/// undrawn stage reports zeros and never reads as complete. The Final never
/// eliminates: its 32 athletes are Finalists with official places elsewhere.
/// Reads never simulate, draw or consume RNG. Shareable by save/season ids.
/// </summary>
public sealed record GetChaosCupSurvivalResponse(
    Guid SaveId,
    int SourceSeasonNumber,
    int SourceSeasonId,
    int RulesVersion,
    int ChaosFormatVersion,
    int FieldSize,
    bool IsComplete,
    IReadOnlyList<GetChaosCupSurvivalResponse.StageSurvival> Stages)
{
    public sealed record StageSurvival(
        int StageIndex,
        int GroupCount,
        int EntrantCount,
        bool IsFinal,
        bool IsDrawn,
        bool IsComplete,
        bool IsProvisional,
        int InitialCount,
        int AdvancedCount,
        int EliminatedCount,
        int PendingCount,
        int FinalistCount,
        IReadOnlyList<BucketCount> ByColor,
        IReadOnlyList<BucketCount> ByDivision);

    public sealed record BucketCount(
        string Key,
        string Label,
        int Initial,
        int Advanced,
        int Eliminated,
        int Pending,
        int Finalists);
}
