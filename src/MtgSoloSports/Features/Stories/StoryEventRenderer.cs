using System.Text.Json;

namespace MtgSoloSports.Features.Stories;

/// <summary>
/// Deterministic authored text templates for structured story events.
/// Pure rendering over persisted <see cref="StoryEventPayload"/> context:
/// no RNG, clock, network or LLM dependency. Unknown future event types
/// render a stable fallback so old readers never break when new types appear.
/// </summary>
public static class StoryEventRenderer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string Render(string eventType, string contextJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextJson);
        StoryEventPayload payload = Parse(contextJson);
        return Render(eventType, payload);
    }

    public static string Render(string eventType, StoryEventPayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(payload);
        return eventType switch
        {
            StoryEventType.FirstStageWin =>
                $"{payload.AthleteName} wins {Leaf(payload.LeagueName)} Stage {payload.StageNumber} in Season {payload.SeasonNumber} for a first stage triumph.",
            StoryEventType.FirstLeagueTitle =>
                $"{payload.AthleteName} wins a first {Leaf(payload.LeagueName)} championship in Season {payload.SeasonNumber}.",
            StoryEventType.LeagueTitleMilestone =>
                $"{payload.AthleteName} claims {Leaf(payload.LeagueName)} title number {payload.TitleCount} in Season {payload.SeasonNumber}.",
            StoryEventType.FirstSuperleagueAppearance =>
                $"{payload.AthleteName} reaches the Superleague for the first time in Season {payload.SeasonNumber}.",
            StoryEventType.Promotion =>
                payload.ViaQualifier == true
                    ? $"{payload.AthleteName} earns promotion from {Leaf(payload.FromLeagueName)} to {Leaf(payload.ToLeagueName)} for Season {payload.SeasonNumber} via the qualifier."
                    : $"{payload.AthleteName} earns promotion from {Leaf(payload.FromLeagueName)} to {Leaf(payload.ToLeagueName)} for Season {payload.SeasonNumber}.",
            StoryEventType.Relegation =>
                payload.ViaQualifier == true
                    ? $"{payload.AthleteName} is relegated from {Leaf(payload.FromLeagueName)} to {Leaf(payload.ToLeagueName)} for Season {payload.SeasonNumber} via the qualifier."
                    : $"{payload.AthleteName} is relegated from {Leaf(payload.FromLeagueName)} to {Leaf(payload.ToLeagueName)} for Season {payload.SeasonNumber}.",
            StoryEventType.ReturnFromPool =>
                $"{payload.AthleteName} returns from the common pool to {Leaf(payload.ToLeagueName)} for Season {payload.SeasonNumber}.",
            StoryEventType.NewRecord =>
                RenderRecord(payload),
            StoryEventType.ColorCupIndividualTitle =>
                $"{payload.AthleteName} wins the Color Cup individual championship in Season {payload.SeasonNumber}.",
            StoryEventType.ColorCupMedal =>
                RenderCupMedal(payload),
            StoryEventType.ColorCupTeamTitle =>
                $"{payload.AthleteName} wins the Color Cup team championship with {Leaf(payload.LeagueName)} in Season {payload.SeasonNumber}.",
            StoryEventType.ColorCupTeamMedal =>
                RenderTeamMedal(payload),
            _ => $"{payload.AthleteName} writes a new chapter in Season {payload.SeasonNumber}.",
        };
    }

    public static StoryEventPayload Parse(string contextJson)
    {
        try
        {
            StoryEventPayload? payload = JsonSerializer.Deserialize<StoryEventPayload>(contextJson, Options);
            return payload ?? throw new InvalidOperationException("Story context is corrupt.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Story context is corrupt.", ex);
        }
    }

    public static string ToJson(StoryEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// Repeat-title milestones available at this stage: the 2nd and 3rd titles
    /// plus every 5th title thereafter (5, 10, 15, ...). The 1st title uses
    /// <see cref="StoryEventType.FirstLeagueTitle"/> instead.
    /// </summary>
    public static bool IsTitleMilestone(int titleCount)
    {
        if (titleCount < 2)
        {
            return false;
        }

        return titleCount is 2 or 3 || titleCount % 5 == 0;
    }

    private static string Leaf(string? value) => string.IsNullOrWhiteSpace(value) ? "the league" : value;

    internal static string RenderCupMedal(StoryEventPayload payload)
    {
        string medal = string.IsNullOrWhiteSpace(payload.Medal) ? "medal" : payload.Medal;
        if (payload.CupRank is not null)
        {
            return $"{payload.AthleteName} wins Color Cup {medal} (rank {payload.CupRank}) in Season {payload.SeasonNumber}.";
        }

        return $"{payload.AthleteName} wins Color Cup {medal} in Season {payload.SeasonNumber}.";
    }

    internal static string RenderTeamMedal(StoryEventPayload payload)
    {
        string medal = string.IsNullOrWhiteSpace(payload.Medal) ? "medal" : payload.Medal;
        string team = string.IsNullOrWhiteSpace(payload.LeagueName) ? "the team" : payload.LeagueName;
        if (payload.CupRank is not null)
        {
            return $"{payload.AthleteName} wins Color Cup team {medal} with {team} (rank {payload.CupRank}) in Season {payload.SeasonNumber}.";
        }

        return $"{payload.AthleteName} wins Color Cup team {medal} with {team} in Season {payload.SeasonNumber}.";
    }

    internal static string RenderRecord(StoryEventPayload payload)
    {
        string label = FormatRecordKey(payload.RecordKey);
        string value = FormatRecordValue(payload.RecordKey, payload.RecordValue);
        if (payload.PriorRecordValue is null or 0)
        {
            return $"{payload.AthleteName} sets a new {label} record of {value} in Season {payload.SeasonNumber}.";
        }

        string prior = FormatRecordValue(payload.RecordKey, payload.PriorRecordValue);
        return $"{payload.AthleteName} breaks the {label} record with {value} in Season {payload.SeasonNumber} (previous {prior}).";
    }

    internal static string FormatRecordKey(string? recordKey) => recordKey switch
    {
        "feeder_titles" => "feeder titles",
        "superleague_titles" => "Superleague titles",
        "total_titles" => "total titles",
        "stage_wins" => "stage wins",
        "round_wins" => "round wins",
        "longest_superleague_tenure" => "Superleague tenure",
        "highest_effective_bonus" => "effective bonus",
        "longest_title_streak" => "title streak",
        "longest_stage_win_streak" => "stage-win streak",
        _ => string.IsNullOrWhiteSpace(recordKey) ? "career" : recordKey.Replace('_', ' '),
    };

    internal static string FormatRecordValue(string? recordKey, int? thousandthsOrCount)
    {
        if (thousandthsOrCount is null)
        {
            return "—";
        }

        if (string.Equals(recordKey, "highest_effective_bonus", StringComparison.Ordinal))
        {
            string sign = thousandthsOrCount.Value >= 0 ? "+" : string.Empty;
            return $"{sign}{(thousandthsOrCount.Value / 1000.0).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}";
        }

        return thousandthsOrCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
