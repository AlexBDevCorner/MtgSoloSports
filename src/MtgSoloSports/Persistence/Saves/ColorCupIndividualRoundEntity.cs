namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Color Cup individual round row per completed odd source season.
/// The individual event is one standard 16-round stage over the 32 selected
/// athletes (MSS-024): active career bonus applies with normal fixed-point
/// scoring/ranking, but no new career bonus is generated and no league
/// <c>Round</c>, <c>Stage</c> or <c>StageStanding</c> rows are created, so
/// normal league season championship totals are untouched. Detailed replay
/// lives in the compact immutable JSON payload, never in one row per athlete
/// placement. RNG before/after states are stored alongside the result so the
/// RNG commit and the sporting result share one transaction.
/// </summary>
public sealed class ColorCupIndividualRoundEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int RoundNumber { get; set; }

    public int RulesVersion { get; set; }

    public long RngBeforeState { get; set; }

    public long RngBeforeStream { get; set; }

    public long RngAfterState { get; set; }

    public long RngAfterStream { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public string PayloadChecksum { get; set; } = string.Empty;
}
