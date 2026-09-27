namespace MtgSoloSports.Features.Records;

/// <summary>
/// Stable record keys for major career records available before Cups.
/// Stored as plain strings in story contexts so future Cup tasks can append
/// keys without rewriting old events. All sporting values are fixed-point
/// thousandths integers where applicable (bonus) or plain counts otherwise;
/// no floating point is used.
/// </summary>
public static class RecordKey
{
    public const string FeederTitles = "feeder_titles";

    public const string SuperleagueTitles = "superleague_titles";

    public const string TotalTitles = "total_titles";

    public const string StageWins = "stage_wins";

    public const string RoundWins = "round_wins";

    public const string SuperleagueAppearances = "superleague_appearances";

    public const string TotalAppearances = "total_appearances";

    public const string LongestSuperleagueTenure = "longest_superleague_tenure";

    public const string Promotions = "promotions";

    public const string Relegations = "relegations";

    public const string HighestEffectiveBonus = "highest_effective_bonus";

    public const string LongestTitleStreak = "longest_title_streak";

    public const string LongestStageWinStreak = "longest_stage_win_streak";

    /// <summary>
    /// All record keys in stable presentation order.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        FeederTitles,
        SuperleagueTitles,
        TotalTitles,
        StageWins,
        RoundWins,
        SuperleagueAppearances,
        TotalAppearances,
        LongestSuperleagueTenure,
        Promotions,
        Relegations,
        HighestEffectiveBonus,
        LongestTitleStreak,
        LongestStageWinStreak,
    ];

    /// <summary>
    /// Records that emit <c>new_record</c> story events on outright breaks.
    /// Participation counts (appearances, promotions, relegations) stay
    /// queryable but never emit record stories: promotions/relegations already
    /// emit movement stories and appearance ties would flood the feed.
    /// </summary>
    public static readonly IReadOnlyList<string> StoryWorthy =
    [
        FeederTitles,
        SuperleagueTitles,
        TotalTitles,
        StageWins,
        RoundWins,
        LongestSuperleagueTenure,
        HighestEffectiveBonus,
        LongestTitleStreak,
        LongestStageWinStreak,
    ];

    public static bool IsKnown(string recordKey) => All.Contains(recordKey, StringComparer.Ordinal);

    public static bool IsStoryWorthy(string recordKey) => StoryWorthy.Contains(recordKey, StringComparer.Ordinal);

    /// <summary>
    /// True for bonus records whose values are fixed-point thousandths.
    /// </summary>
    public static bool IsBonus(string recordKey) => string.Equals(recordKey, HighestEffectiveBonus, StringComparison.Ordinal);
}
