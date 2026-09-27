using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Stories.ListRecent;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Stories.ListAthleteStories;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one athlete's story
/// feed for profile display: newest structured events first with deterministic
/// rendered text. Read-only; never resimulates and never touches round payloads.
/// </summary>
public sealed class ListAthleteStoriesHandler
{
    private readonly SaveStore _store;

    public ListAthleteStoriesHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListRecentStoriesResponse> HandleAsync(Guid saveId, int athleteId, int take = ListRecentStoriesHandler.DefaultTake, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (athleteId <= 0)
        {
            throw new ArgumentException("Athlete id must be positive.", nameof(athleteId));
        }

        int limited = Math.Clamp(take, 1, ListRecentStoriesHandler.MaxTake);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        bool exists = await context.SaveAthletes.AsNoTracking().AnyAsync(e => e.Id == athleteId, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            throw new AthleteStoriesNotFoundException($"Athlete {athleteId} does not exist in save '{saveId:D}'.");
        }

        List<StoryEventEntity> rows = await context.StoryEvents
            .AsNoTracking()
            .Where(e => e.SaveAthleteId == athleteId)
            .OrderByDescending(e => e.Id)
            .Take(limited)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        string name = await context.SaveAthletes
            .AsNoTracking()
            .Where(e => e.Id == athleteId)
            .Select(e => e.Name)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = new() { [athleteId] = name };
        return new ListRecentStoriesResponse(saveId, ListRecentStoriesHandler.Map(rows, names));
    }
}
