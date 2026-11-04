namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Single-row table (Id always 1) holding the save-specific Cup selection
/// formula activation (MSS-132). <see cref="EffectiveFromSeasonNumber"/> is the
/// first source season that uses the approved 35/30/20/5/10 weights; earlier
/// source seasons keep their persisted snapshot weights (legacy 35/30/25/10).
/// <see cref="FormulaVersion"/> pins the activation (1 is the MSS-132 upgrade).
/// New saves use 1 (current from the first eligible edition); legacy saves
/// backfill to max(CurrentSeason + 1, Chaos activation) so the currently
/// started source season keeps its old formula. Independently identifiable
/// from <see cref="ChaosCupActivationEntity"/>; an existing Chaos activation
/// never blocks this later upgrade.
/// </summary>
public sealed class CupSelectionFormulaActivationEntity
{
    public int Id { get; set; }

    public int EffectiveFromSeasonNumber { get; set; }

    public int FormulaVersion { get; set; }
}
