namespace MtgSoloSports.Features.Records.GetRecords;

/// <summary>
/// One scoring-record holder with enough season/competition context to
/// identify where the achievement happened. Athlete records carry an athlete
/// id/name; pure team records carry only a team key/name. Values are
/// fixed-point thousandths points.
/// </summary>
public sealed record ScoringRecordHolderEntry(
    int? AthleteId,
    string? AthleteName,
    string TeamKey,
    string TeamName,
    int Value,
    string ValueDisplay,
    int SeasonNumber,
    string Competition,
    string? LeagueName,
    int? StageNumber,
    int? RoundNumber,
    int? GroupNumber);
