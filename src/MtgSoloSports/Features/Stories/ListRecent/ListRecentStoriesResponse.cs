namespace MtgSoloSports.Features.Stories.ListRecent;

/// <summary>
/// Recent story feed response ordered newest first.
/// </summary>
public sealed record ListRecentStoriesResponse(
    Guid SaveId,
    IReadOnlyList<StoryEventDto> Stories);
