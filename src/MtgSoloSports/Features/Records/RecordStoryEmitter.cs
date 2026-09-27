using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records;

/// <summary>
/// Detects outright record breaks at season finalization and emits
/// <c>new_record</c> story events transactionally. Ties never emit: all
/// athletes sharing the maximum are joint holders, and only a strictly higher
/// value replaces the holder list. Prior maxima come from existing
/// <c>new_record</c> story history (parsed in memory, never round payloads);
/// current maxima come from <see cref="RecordLoader"/> plus
/// <see cref="RecordCalculator"/>. Idempotent via per-athlete dedup keys.
/// Only <see cref="RecordKey.StoryWorthy"/> keys emit stories; participation
/// counts stay queryable without flooding the feed.
/// </summary>
public static class RecordStoryEmitter
{
    /// <summary>
    /// Emits record stories for outright breaks in the caller's transaction.
    /// Must run after honours sync and career-projection refresh so current
    /// values include the just-finalized season. Returns true when any story
    /// was staged.
    /// </summary>
    public static async Task<bool> EmitBreaksAsync(
        SaveDbContext context,
        SeasonEntity season,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        RecordLoader.RecordInputs inputs = await RecordLoader.LoadAsync(context, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RecordCalculator.RecordHolders> current =
            RecordCalculator.ComputeAll(
                inputs.AthleteNames,
                inputs.SeasonStandings,
                inputs.StageStandings,
                inputs.Memberships,
                inputs.Careers,
                inputs.Promotions);
        Dictionary<string, int> priorMaxima = await LoadPriorMaximaAsync(context, cancellationToken).ConfigureAwait(false);
        bool emitted = false;
        foreach (RecordCalculator.RecordHolders record in current)
        {
            emitted |= await EmitSingleRecordAsync(context, season, inputs.AthleteNames, record, priorMaxima, cancellationToken).ConfigureAwait(false);
        }

        if (emitted)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    internal static async Task<bool> EmitSingleRecordAsync(
        SaveDbContext context,
        SeasonEntity season,
        Dictionary<int, string> names,
        RecordCalculator.RecordHolders record,
        Dictionary<string, int> priorMaxima,
        CancellationToken cancellationToken)
    {
        if (!RecordKey.IsStoryWorthy(record.RecordKey))
        {
            return false;
        }

        if (record.HolderAthleteIds.Count == 0 || record.Value <= 0)
        {
            return false;
        }

        priorMaxima.TryGetValue(record.RecordKey, out int prior);
        if (record.Value <= prior)
        {
            return false;
        }

        bool emitted = false;
        foreach (int athleteId in record.HolderAthleteIds)
        {
            names.TryGetValue(athleteId, out string? name);
            emitted |= await Stories.StoryEventEmitter.TryEmitAsync(
                context,
                athleteId,
                Stories.StoryEventType.NewRecord,
                RecordDedup(record.RecordKey, season.SeasonNumber, record.Value),
                season.SeasonNumber,
                null,
                new Stories.StoryEventPayload(
                    name ?? $"Athlete {athleteId}",
                    season.SeasonNumber,
                    RecordKey: record.RecordKey,
                    RecordValue: record.Value,
                    PriorRecordValue: prior),
                cancellationToken).ConfigureAwait(false);
        }

        return emitted;
    }

    internal static string RecordDedup(string recordKey, int seasonNumber, int value) =>
        $"record:{recordKey}:s{seasonNumber}:v{value}";

    /// <summary>
    /// Loads prior maxima per record key from existing <c>new_record</c> story
    /// history. Parses <c>ContextJson</c> in memory; never touches round
    /// payloads. Corrupt contexts abort.
    /// </summary>
    internal static async Task<Dictionary<string, int>> LoadPriorMaximaAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<StoryProbe> rows = await context.StoryEvents
            .AsNoTracking()
            .Where(e => e.EventType == Stories.StoryEventType.NewRecord)
            .Select(e => new StoryProbe(e.ContextJson))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, int> maxima = new(StringComparer.Ordinal);
        foreach (StoryProbe row in rows)
        {
            (string? key, int value) = ParseRecordContext(row.ContextJson);
            if (key is null || !RecordKey.IsKnown(key))
            {
                continue;
            }

            if (!maxima.TryGetValue(key, out int existing) || value > existing)
            {
                maxima[key] = value;
            }
        }

        return maxima;
    }

    internal static (string? RecordKey, int Value) ParseRecordContext(string contextJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(contextJson);
            string? key = null;
            int value = 0;
            if (document.RootElement.TryGetProperty("recordKey", out JsonElement keyElement) &&
                keyElement.ValueKind == JsonValueKind.String)
            {
                key = keyElement.GetString();
            }

            if (document.RootElement.TryGetProperty("recordValue", out JsonElement valueElement) &&
                valueElement.ValueKind == JsonValueKind.Number &&
                valueElement.TryGetInt32(out int parsed))
            {
                value = parsed;
            }

            return (key, value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Record story context is corrupt.", ex);
        }
    }

    private sealed record StoryProbe(string ContextJson);
}
