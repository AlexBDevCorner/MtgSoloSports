namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One league (competition) within a season. Season 1 has exactly eight feeder
/// leagues, one per sporting color, and no Superleague row.
/// League membership itself lives in <see cref="SeasonMembershipEntity"/>; this
/// table only identifies the league.
/// <see cref="FeederDivision"/> carries the feeder subdivision explicitly
/// (None for Superleague, First/Second/Third for feeders) so later tiers never
/// infer division from league names. Historical v1 feeder rows surface as
/// Feeder 1.
/// </summary>
public sealed class LeagueEntity
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int SportingColor { get; set; }

    public int Kind { get; set; }

    public int FeederDivision { get; set; }

    public string Name { get; set; } = string.Empty;
}
