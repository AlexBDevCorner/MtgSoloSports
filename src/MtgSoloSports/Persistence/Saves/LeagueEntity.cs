namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One league (competition) within a season. Season 1 has exactly eight feeder
/// leagues, one per sporting color, and no Superleague row.
/// League membership itself lives in <see cref="SeasonMembershipEntity"/>; this
/// table only identifies the league.
/// </summary>
public sealed class LeagueEntity
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int SportingColor { get; set; }

    public int Kind { get; set; }

    public string Name { get; set; } = string.Empty;
}
