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
}
