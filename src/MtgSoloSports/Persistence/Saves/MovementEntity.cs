namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One durable postseason movement record per athlete per destination season.
/// MSS-014 persists exactly 32 <see cref="MovementKind.InauguralPromotion"/>
/// rows (Season 1 feeder places 1-4 into the Season 2 Superleague) so promotion
/// history survives independently of membership rows. Later slices reuse this
/// table for automatic/qualifier/rebalancing movements with new
/// <see cref="MovementKind"/> values.
/// <see cref="FromSeasonRank"/> is the athlete's final rank in its source
/// feeder league (1-4 for the inaugural transition).
/// <see cref="SportingColor"/> is the athlete's sporting color at movement time
/// and determines the returning feeder color for future relegation flows.
/// </summary>
public sealed class MovementEntity
{
    public int Id { get; set; }

    public int SaveAthleteId { get; set; }

    public int FromSeasonId { get; set; }

    public int ToSeasonId { get; set; }

    public int FromLeagueId { get; set; }

    public int ToLeagueId { get; set; }

    public int Kind { get; set; }

    public int FromSeasonRank { get; set; }

    public int SportingColor { get; set; }
}
