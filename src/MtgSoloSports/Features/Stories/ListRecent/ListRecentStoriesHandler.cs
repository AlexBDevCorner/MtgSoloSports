using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Stories.ListRecent;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads the persisted recent
/// story feed for dashboard display: newest structured events first with
/// deterministic rendered text. Read-only; never resimulates and never touches
/// round payloads.
/// </summary>
public sealed class ListRecentStoriesHandler
{
    public const int DefaultTake = 20;

    public const int MaxTake = 100;

    private readonly SaveStore _store;

    public ListRecentStoriesHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListRecentStoriesResponse> HandleAsync(Guid saveId, int take = DefaultTake, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        int limited = Math.Clamp(take, 1, MaxTake);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        List<StoryEventEntity> rows = await context.StoryEvents
            .AsNoTracking()
            .OrderByDescending(e => e.Id)
            .Take(limited)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await LoadNamesAsync(context, rows, cancellationToken).ConfigureAwait(false);
        return new ListRecentStoriesResponse(saveId, Map(rows, names));
    }

    internal static async Task<Dictionary<int, string>> LoadNamesAsync(
        SaveDbContext context,
        List<StoryEventEntity> rows,
        CancellationToken cancellationToken)
    {
        HashSet<int> ids = rows.Select(r => r.SaveAthleteId).ToHashSet();
        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        return await context.SaveAthletes
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static IReadOnlyList<StoryEventDto> Map(List<StoryEventEntity> rows, Dictionary<int, string> names)
    {
        List<StoryEventDto> items = new(rows.Count);
        foreach (StoryEventEntity row in rows)
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            string text = StoryEventRenderer.Render(row.EventType, row.ContextJson);
            items.Add(new StoryEventDto(
                row.Id,
                row.EventType,
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}",
                row.SeasonNumber,
                row.StageNumber,
                row.ContextJson,
                text));
        }

        return items;
    }
}
