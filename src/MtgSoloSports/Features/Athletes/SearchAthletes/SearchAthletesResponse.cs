namespace MtgSoloSports.Features.Athletes.SearchAthletes;

/// <summary>
/// Immutable paged search response. Filters and sort are applied before
/// pagination; <c>TotalCount</c> is the full filtered count.
/// </summary>
public sealed record SearchAthletesResponse(
    Guid SaveId,
    int TotalCount,
    int Skip,
    int Take,
    IReadOnlyList<AthleteSearchResultDto> Results);
