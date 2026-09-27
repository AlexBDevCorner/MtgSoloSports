namespace MtgSoloSports.Features.Records.GetRecords;

/// <summary>
/// One record holder for athlete navigation.
/// </summary>
public sealed record RecordHolderEntry(
    int AthleteId,
    string AthleteName);
