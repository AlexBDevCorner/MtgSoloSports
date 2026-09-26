namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One row per simulated league round. Detailed replay lives in the compact
/// immutable JSON payload, never in one row per athlete placement: the payload
/// holds finishing order, base points, stage-start active bonus, final points
/// and before/after cumulative stage totals plus rank movement for all 32
/// athletes. RNG before/after states are stored alongside the result so the
/// RNG commit and the sporting result share one transaction.
/// </summary>
public sealed class RoundEntity
{
    public int Id { get; set; }

    public int SeasonId { get; set; }

    public int LeagueId { get; set; }

    public int StageId { get; set; }

    public int StageNumber { get; set; }

    public int RoundNumber { get; set; }

    public int RulesVersion { get; set; }

    public long RngBeforeState { get; set; }

    public long RngBeforeStream { get; set; }

    public long RngAfterState { get; set; }

    public long RngAfterStream { get; set; }

    public string PayloadJson { get; set; } = string.Empty;

    public string PayloadChecksum { get; set; } = string.Empty;
}
