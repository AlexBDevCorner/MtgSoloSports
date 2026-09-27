namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Color Cup team round row per group per round for a completed
/// odd source season. The team event runs four rank groups (all #1 athletes,
/// then #2, #3, #4) with exactly eight athletes and eight rounds per group
/// (MSS-025): active career bonus applies with normal fixed-point scoring,
/// but no new career bonus is generated and no league <c>Round</c>,
/// <c>Stage</c> or <c>StageStanding</c> rows are created. Detailed replay lives
/// in the compact immutable compressed payload (JSON + Brotli <c>br1:</c>,
/// matching normal league round history), never in one row per athlete
/// placement. RNG before/after states are stored alongside the result so the
/// RNG commit and the sporting result share one transaction.
/// </summary>
public sealed class ColorCupTeamRoundEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public int GroupNumber { get; set; }

    public int RoundNumber { get; set; }

    public int RulesVersion { get; set; }

    public long RngBeforeState { get; set; }

    public long RngBeforeStream { get; set; }

    public long RngAfterState { get; set; }

    public long RngAfterStream { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public string PayloadChecksum { get; set; } = string.Empty;
}
