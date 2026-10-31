namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One Chaos Cup stage-draw assignment row per entrant per stage (MSS-099).
/// Staged groups and draw share one table: (StageIndex, GroupNumber) identifies the
/// 32-athlete group, <see cref="SaveAthleteId"/> places one entrant exactly once
/// per stage, and <see cref="DrawIndex"/> records the within-group draw order
/// (0..31) for auditing. Per-stage draw RNG and checksum are duplicated per row
/// (matching the Type Cup draw pattern) and invariants require them consistent
/// per (SourceSeasonId, StageIndex) so retries cannot reroll the draw.
/// Uniqueness on (SourceSeasonId, StageIndex, SaveAthleteId) rejects duplicate
/// stage membership; uniqueness on (SourceSeasonId, StageIndex, GroupNumber,
/// DrawIndex) rejects duplicate draw positions.
/// </summary>
public sealed class ChaosCupDrawEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int StageIndex { get; set; }

    public int GroupNumber { get; set; }

    public int SaveAthleteId { get; set; }

    public int DrawIndex { get; set; }

    public int GroupSize { get; set; }

    public int StageGroupCount { get; set; }

    public int RulesVersion { get; set; }

    public int ChaosFormatVersion { get; set; }

    public long RngBeforeState { get; set; }

    public long RngBeforeStream { get; set; }

    public long RngAfterState { get; set; }

    public long RngAfterStream { get; set; }

    public string DrawChecksum { get; set; } = string.Empty;
}
