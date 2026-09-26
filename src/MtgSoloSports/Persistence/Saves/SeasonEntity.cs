namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One season row per save. Season 1 is created atomically with the save
/// universe and has no Superleague; later seasons are added by postseason slices.
/// </summary>
public sealed class SeasonEntity
{
    public int Id { get; set; }

    public int SeasonNumber { get; set; }

    public bool HasSuperleague { get; set; }
}
