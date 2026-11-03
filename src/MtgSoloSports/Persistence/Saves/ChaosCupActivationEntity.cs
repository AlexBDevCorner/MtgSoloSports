namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// Single-row table (Id always 1) holding the save-specific Chaos Cup activation.
/// <see cref="EffectiveFromSeasonNumber"/> is the first source season that runs Chaos:
/// new saves use 1 (Season 1 inaugural branch included); saves created before MSS-106
/// backfill to max persisted season + 1 on first migration so current/historic seasons
/// never gain an injected tournament. <see cref="ChaosFormatVersion"/> pins the format.
/// </summary>
public sealed class ChaosCupActivationEntity
{
    public int Id { get; set; }

    public int EffectiveFromSeasonNumber { get; set; }

    public int ChaosFormatVersion { get; set; }
}
