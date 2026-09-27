using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Stories;

/// <summary>
/// Transactional idempotent story-event emission. Callers invoke this inside
/// their sporting transaction (stage completion, season finalization, inaugural
/// and automatic movement, qualifier, feeder rebalancing) before commit; the
/// row commits atomically with the sporting result and RNG state. Emission is
/// idempotent: the unique index on <c>(SaveAthleteId, EventType, DedupKey)</c>
/// plus an existence check skips duplicates on retries and reloads. Consumes no
/// sporting RNG and performs no rendering; wording is derived separately by
/// <see cref="StoryEventRenderer"/>.
/// </summary>
public static class StoryEventEmitter
{
    public static async Task<bool> TryEmitAsync(
        SaveDbContext context,
        int saveAthleteId,
        string eventType,
        string dedupKey,
        int seasonNumber,
        int? stageNumber,
        StoryEventPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);
        ArgumentNullException.ThrowIfNull(payload);
        if (saveAthleteId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(saveAthleteId));
        }

        if (seasonNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(seasonNumber));
        }

        bool exists = await context.StoryEvents.AnyAsync(
            e => e.SaveAthleteId == saveAthleteId && e.EventType == eventType && e.DedupKey == dedupKey,
            cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return false;
        }

        context.StoryEvents.Add(new StoryEventEntity
        {
            SaveAthleteId = saveAthleteId,
            EventType = eventType,
            DedupKey = dedupKey,
            SeasonNumber = seasonNumber,
            StageNumber = stageNumber,
            ContextJson = StoryEventRenderer.ToJson(payload),
        });
        return true;
    }

    public static string MovementDedup(int fromSeasonNumber, int toSeasonNumber) =>
        $"s{fromSeasonNumber}->s{toSeasonNumber}";

    public static string TitleDedup(int titleCount) => $"titles:{titleCount}";

    public const string FirstDedup = "first";

    /// <summary>
    /// True when the athlete already occupied a Superleague membership in any
    /// season other than <paramref name="excludeSeasonId"/>. Used to emit the
    /// first-Superleague-appearance event exactly once per athlete.
    /// </summary>
    public static async Task<bool> HasPriorSuperleagueAppearanceAsync(
        SaveDbContext context,
        int saveAthleteId,
        int excludeSeasonId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool hasPrior = await (from membership in context.SeasonMemberships
                               join league in context.Leagues on membership.LeagueId equals league.Id
                               where membership.SaveAthleteId == saveAthleteId
                                   && membership.SeasonId != excludeSeasonId
                                   && league.Kind == (int)Persistence.Saves.LeagueKind.Superleague
                               select membership.Id).AnyAsync(cancellationToken).ConfigureAwait(false);
        return hasPrior;
    }
}
