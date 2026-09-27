namespace MtgSoloSports.Features.Records.GetHallOfFame;

/// <summary>
/// Immutable Hall of Fame response: career leaders ordered by official titles
/// then stage/round wins with deterministic athlete navigation ids.
/// </summary>
public sealed record GetHallOfFameResponse(
    Guid SaveId,
    int Take,
    int TotalAthletes,
    IReadOnlyList<HallOfFameEntry> Leaders);
