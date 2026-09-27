namespace MtgSoloSports.Features.Stories;

/// <summary>
/// Stable structured story-event types. Stored as plain strings so future
/// Cup and record tasks can add types without rewriting old events.
/// </summary>
public static class StoryEventType
{
    public const string FirstStageWin = "first_stage_win";

    public const string FirstLeagueTitle = "first_league_title";

    public const string LeagueTitleMilestone = "league_title_milestone";

    public const string FirstSuperleagueAppearance = "first_superleague_appearance";

    public const string Promotion = "promotion";

    public const string Relegation = "relegation";

    public const string ReturnFromPool = "return_from_pool";

    public const string NewRecord = "new_record";

    /// <summary>
    /// Known types at this stage. Future tasks append without changing these.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownTypes =
    [
        FirstStageWin,
        FirstLeagueTitle,
        LeagueTitleMilestone,
        FirstSuperleagueAppearance,
        Promotion,
        Relegation,
        ReturnFromPool,
        NewRecord,
    ];

    public static bool IsKnown(string eventType) => KnownTypes.Contains(eventType, StringComparer.Ordinal);
}
