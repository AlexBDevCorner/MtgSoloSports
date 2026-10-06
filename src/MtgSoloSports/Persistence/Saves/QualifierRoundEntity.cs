namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable qualifier round row per postseason transition per qualifier event.
/// A qualifier behaves like one standard 16-round stage (MSS-016): active career
/// bonus applies with normal fixed-point scoring/ranking, but no new round or
/// stage career bonus is generated. Detailed replay lives in the compact
/// immutable JSON payload, never in one row per athlete placement. RNG
/// before/after states are stored alongside the result so the RNG commit and
/// the sporting result share one transaction. Qualifier rounds never create
/// <c>Round</c>, <c>Stage</c> or <c>StageStanding</c> rows, so normal league
/// season championship totals are untouched.
/// MSS-058 generalizes to 17 qualifier events per ordinary tiered transition:
/// <see cref="QualifierBoundary"/> plus <see cref="QualifierSportingColor"/>
/// identify the event alongside the season transition. Superleague carries
/// color sentinel -1; feeder boundaries carry 0..7. Uniqueness includes the
/// event identity so rows from different qualifiers never collide.
/// </summary>
public sealed class QualifierRoundEntity
{
    public int Id { get; set; }

    public int FromSeasonId { get; set; }

    public int ToSeasonId { get; set; }

    /// <summary>
    /// Adjacent-tier boundary: 0 Superleague↔F1, 1 F1↔F2, 2 F2↔F3.
    /// </summary>
    public int QualifierBoundary { get; set; }

    /// <summary>
    /// Sporting color for feeder boundaries (0..7); -1 for Superleague.
    /// </summary>
    public int QualifierSportingColor { get; set; }

    public int RoundNumber { get; set; }

    public int RulesVersion { get; set; }

    public long RngBeforeState { get; set; }

    public long RngBeforeStream { get; set; }

    public long RngAfterState { get; set; }

    public long RngAfterStream { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public string PayloadChecksum { get; set; } = string.Empty;
}
