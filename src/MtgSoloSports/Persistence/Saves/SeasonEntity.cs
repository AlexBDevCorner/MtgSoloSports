namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One season row per save. Season 1 is created atomically with the save
/// universe and has no Superleague; later seasons are added by postseason slices.
/// <see cref="IsComplete"/> becomes true only when every active league has
/// completed all 32 stages and deterministic <c>SeasonStanding</c> rows
/// (including each feeder champion) are committed in the same transaction.
/// </summary>
public sealed class SeasonEntity
{
    public int Id { get; set; }

    public int SeasonNumber { get; set; }

    public bool HasSuperleague { get; set; }

    public bool IsComplete { get; set; }
}
