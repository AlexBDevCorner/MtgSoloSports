namespace MtgSoloSports.Persistence.Saves;

/// <summary>
/// One immutable Cup selection report per source season: the ranking behind the
/// selected teams (who made the cut, who just missed it and why), written in
/// the same transaction as the selection rows. The selection event replays
/// this payload and never recomputes it, because the inputs (honours, bonus,
/// nationality) keep changing after the selection. Stored as one compact
/// compressed payload (JSON + Brotli <c>br1:</c>) rather than one row per
/// ranked athlete. <see cref="Cup"/> is <c>ColorCup</c> or <c>TypeCup</c>.
/// Saves whose selections predate this table simply have no report.
/// </summary>
public sealed class CupSelectionReportEntity
{
    public int Id { get; set; }

    public int SourceSeasonId { get; set; }

    public int SourceSeasonNumber { get; set; }

    public string Cup { get; set; } = string.Empty;

    public int RulesVersion { get; set; }

    public string PayloadJson { get; set; } = string.Empty;
}
