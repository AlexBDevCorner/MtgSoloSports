using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.ListSeasons;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Lists seasons for history
/// navigation from normalized tables only. Never selects
/// <c>Rounds.PayloadJson</c> and never decompresses round payloads; the query
/// projects only season/league/stage identity plus counts.
/// Read-only: no lock, no RNG access, no mutation.
/// </summary>
public sealed class ListHistorySeasonsHandler
{
    private readonly SaveStore _store;

    public ListHistorySeasonsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListHistorySeasonsResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        List<SeasonEntity> seasons = await context.Seasons
            .AsNoTracking()
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Normalized counts only: league/stage identity, never round payloads.
        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Select(e => new LeagueEntity { Id = e.Id, SeasonId = e.SeasonId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<StageEntity> stages = await context.Stages
            .AsNoTracking()
            .Select(e => new StageEntity { SeasonId = e.SeasonId, IsComplete = e.IsComplete })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Round counts without payloads: project identity columns only so the
        // payload column is never transferred or decompressed.
        List<RoundProbe> rounds = await context.Rounds
            .AsNoTracking()
            .Select(e => new RoundProbe(e.SeasonId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, int> leaguesBySeason = leagues
            .GroupBy(e => e.SeasonId)
            .ToDictionary(g => g.Key, g => g.Count());
        Dictionary<int, int> completedStagesBySeason = stages
            .Where(e => e.IsComplete)
            .GroupBy(e => e.SeasonId)
            .ToDictionary(g => g.Key, g => g.Count());
        Dictionary<int, int> roundsBySeason = rounds
            .GroupBy(e => e.SeasonId)
            .ToDictionary(g => g.Key, g => g.Count());

        List<HistorySeasonSummary> summaries = new(seasons.Count);
        foreach (SeasonEntity season in seasons)
        {
            leaguesBySeason.TryGetValue(season.Id, out int competitions);
            completedStagesBySeason.TryGetValue(season.Id, out int completedStages);
            roundsBySeason.TryGetValue(season.Id, out int totalRounds);
            summaries.Add(new HistorySeasonSummary(
                season.SeasonNumber,
                season.HasSuperleague,
                season.IsComplete,
                competitions,
                completedStages,
                totalRounds));
        }

        return new ListHistorySeasonsResponse(saveId, summaries);
    }

    private sealed record RoundProbe(int SeasonId);
}
