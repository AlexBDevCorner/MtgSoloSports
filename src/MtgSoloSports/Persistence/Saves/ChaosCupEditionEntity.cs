namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One Chaos Cup edition per source season (MSS-099). The edition freezes the
/// approved format (<see cref="SimulationKernel.Cups.ChaosCupFormat.ChaosFormatVersion"/>)
/// alongside the save rules version so future format evolution never silently
/// reinterprets history. <see cref="RngBeforeState"/>/<see cref="RngAfterState"/>
/// bracket the edition seed checkpoint (entrant freeze plus first-draw seed);
/// per-stage draws and per-group rounds carry their own RNG identities.
/// Uniqueness on <see cref="SourceSeasonId"/> rejects duplicate editions.
/// </summary>
public sealed class ChaosCupEditionEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int RulesVersion { get; set; }

    public int ChaosFormatVersion { get; set; }

    public int FieldSize { get; set; }

    public long RngBeforeState { get; set; }

    public long RngBeforeStream { get; set; }

    public long RngAfterState { get; set; }

    public long RngAfterStream { get; set; }

    public string EditionChecksum { get; set; } = string.Empty;
}
