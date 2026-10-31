namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Chaos Cup group round row per staged group per game round (MSS-099).
/// Each 32-athlete group plays 16 rounds with active bonus applied but not earned;
/// no league <c>Round</c>/<c>Stage</c> rows are created. Detailed replay lives in the
/// compact immutable compressed payload (JSON + Brotli <c>br1:</c>, matching normal
/// league round history), never in one row per athlete placement. Per-group
/// RNG before/after states give each group an independent RNG identity so group
/// simulation order cannot influence other groups; the RNG commit and the sporting
/// result share one transaction. Uniqueness on (SourceSeasonId, StageIndex,
/// GroupNumber, RoundNumber) rejects duplicate rounds and supports resumable
/// partially played groups across restart.
/// </summary>
public sealed class ChaosCupRoundEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int StageIndex { get; set; }

    public int GroupNumber { get; set; }

    public int RoundNumber { get; set; }

    public int RulesVersion { get; set; }

    public int ChaosFormatVersion { get; set; }

    public long RngBeforeState { get; set; }

    public long RngBeforeStream { get; set; }

    public long RngAfterState { get; set; }

    public long RngAfterStream { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public string PayloadChecksum { get; set; } = string.Empty;
}
