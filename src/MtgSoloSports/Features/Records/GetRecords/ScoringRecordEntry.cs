namespace MtgSoloSports.Features.Records.GetRecords;

/// <summary>
/// One scoring record with its current value and joint holders.
/// Values are fixed-point thousandths points. Category groups related
/// records (League, Individual Cups, Team Cups); Scope is the display
/// label for the league or event type. ScopeKey is the stable scope
/// identity (league scope or event scope from <see cref="ScoreRecordKey"/>)
/// and Metric is the stable metric identity ("round" | "stage" | "season" |
/// "total" | "team_round") so grids place cells from structured fields,
/// never from display labels. Both are additive (MSS-140).
/// </summary>
public sealed record ScoringRecordEntry(
    string RecordKey,
    string Label,
    string Category,
    string Scope,
    int Value,
    string ValueDisplay,
    bool IsVacant,
    IReadOnlyList<ScoringRecordHolderEntry> Holders,
    string ScopeKey = "",
    string Metric = "");
