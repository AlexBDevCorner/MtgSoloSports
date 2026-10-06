namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One durable postseason movement record per athlete per destination season.
/// MSS-014 persists exactly 32 <see cref="MovementKind.InauguralPromotion"/>
/// rows (Season 1 feeder places 1-4 into the Season 2 Superleague) so promotion
/// history survives independently of membership rows. MSS-015 reuses this
/// table for normal automatic movement: 8 <see cref="MovementKind.AutomaticPromotion"/>,
/// 8 <see cref="MovementKind.AutomaticRelegation"/>, 8
/// <see cref="MovementKind.QualifierIncumbent"/> and 24
/// <see cref="MovementKind.QualifierChallenger"/> rows (48 total) per
/// completed Superleague season. MSS-017 adds <see cref="MovementKind.RebalanceDraw"/>
/// (pool to feeder) and <see cref="MovementKind.RebalanceDisplacement"/>
/// (feeder to pool) rows without rewriting existing rows. MSS-058 adds feeder
/// automatic movement across adjacent tiers without rewriting existing rows:
/// per color, F2 1-8 and F3 1-8 auto-promote
/// (<see cref="MovementKind.FeederAutomaticPromotion"/>), F1 25-32 and F2 25-32
/// auto-relegate (<see cref="MovementKind.FeederAutomaticRelegation"/>), F1/F2
/// 17-24 defend as incumbents
/// (<see cref="MovementKind.FeederQualifierIncumbent"/>), F2/F3 9-16 challenge
/// (<see cref="MovementKind.FeederQualifierChallenger"/>): 64 per color, 512
/// total per ordinary tiered transition, plus the 48 Superleague rows. MSS-059
/// adds structural tier-cascade moves between adjacent feeder divisions
/// (<see cref="MovementKind.RebalanceUp"/> F2→F1/F3→F2 and
/// <see cref="MovementKind.RebalanceDown"/> F1→F2/F2→F3) so the common pool
/// only ever connects directly to F3 (draws pool→F3, displacements F3→pool).
/// An athlete may hold one competitive plus one structural row per destination
/// season (distinct <see cref="MovementKind"/> values; uniqueness is
/// ToSeasonId+SaveAthleteId+Kind), so sporting promotion/relegation history
/// stays distinct from structural repair. Later slices add new
/// <see cref="MovementKind"/> values without rewriting existing rows.
/// <see cref="FromSeasonRank"/> is the athlete's final rank in its source
/// league (1-32), or 0 for pool-origin draws with no source rank.
/// <see cref="SportingColor"/> is the athlete's sporting color
/// at movement time and determines the returning feeder color for relegation
/// flows. Qualifier-candidate rows are provisional markers only: qualifier
/// winners (MSS-016/MSS-058) and feeder rebalancing (MSS-017) resolve the final
/// next-season roster. Every normal movement is between adjacent levels only;
/// source/destination tiers derive from <c>FromLeagueId</c>/<c>ToLeagueId</c>
/// via league levels and must never skip a division.
/// Pool transfers use league id 0 as the pool sentinel: draws carry
/// <c>FromLeagueId = 0</c> with the feeder as destination, displacements carry
/// the feeder as origin with <c>ToLeagueId = 0</c>.
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
