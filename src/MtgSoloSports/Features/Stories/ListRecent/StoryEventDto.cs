namespace MtgSoloSports.Features.Stories.ListRecent;

/// <summary>
/// Immutable story feed item: structured type plus context plus deterministic
/// rendered text. The text is derived, never the source of truth.
/// </summary>
public sealed record StoryEventDto(
    int Id,
    string EventType,
    int AthleteId,
    string AthleteName,
    int SeasonNumber,
    int? StageNumber,
    string ContextJson,
    string Text);
