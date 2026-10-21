namespace MtgSoloSports.Features.Records.GetHallOfFame;

/// <summary>
/// Immutable Hall of Fame response: career leaders ordered by documented
/// career performance points with deterministic tie-breaks and athlete ids.
/// Titles are period-scoped supporting statistics for the selected range.
/// </summary>
public sealed record GetHallOfFameResponse(
    Guid SaveId,
    int Take,
    int TotalAthletes,
    IReadOnlyList<HallOfFameEntry> Leaders,
    int? FromSeason,
    int? ToSeason,
    int MinSeason,
    int MaxSeason,
    string WeightsVersion);
