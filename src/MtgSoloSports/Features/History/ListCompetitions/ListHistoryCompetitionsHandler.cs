using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.History.ListCompetitions;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Lists competitions
/// (leagues) for one historical season from normalized tables only. Never
/// selects <c>Rounds.PayloadJson</c> and never decompresses round payloads.
/// Read-only: no lock, no RNG access, no mutation.
/// </summary>
public sealed class ListHistoryCompetitionsHandler
{
    private readonly SaveStore _store;

    public ListHistoryCompetitionsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListHistoryCompetitionsResponse> HandleAsync(Guid saveId, int seasonNumber, CancellationToken cancellationToken = default)
    {
        ValidateIds(saveId, seasonNumber);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity season = await LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        CompetitionQuery query = await LoadQueryAsync(context, season, cancellationToken).ConfigureAwait(false);
        return MapResponse(saveId, season, query);
    }

    internal static void ValidateIds(Guid saveId, int seasonNumber)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(seasonNumber));
        }
    }

    internal sealed record CompetitionQuery(
        List<LeagueEntity> Leagues,
        List<StageProbe> Stages,
        List<RoundProbe> Rounds,
        HashSet<int> LeaguesWithFinal);

    internal sealed record StageProbe(int LeagueId, bool IsComplete);

    internal sealed record RoundProbe(int LeagueId);

    internal static async Task<CompetitionQuery> LoadQueryAsync(SaveDbContext context, SeasonEntity season, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        List<LeagueEntity> leagues = await LoadLeaguesAsync(context, season, cancellationToken).ConfigureAwait(false);
        List<StageProbe> stages = await LoadStagesAsync(context, season, cancellationToken).ConfigureAwait(false);
        List<RoundProbe> rounds = await LoadRoundsAsync(context, season, cancellationToken).ConfigureAwait(false);
        HashSet<int> finals = await LoadFinalsAsync(context, season, cancellationToken).ConfigureAwait(false);
        return new CompetitionQuery(leagues, stages, rounds, finals);
    }

    internal static async Task<List<LeagueEntity>> LoadLeaguesAsync(SaveDbContext context, SeasonEntity season, CancellationToken cancellationToken)
    {
        return await context.Leagues
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<List<StageProbe>> LoadStagesAsync(SaveDbContext context, SeasonEntity season, CancellationToken cancellationToken)
    {
        return await context.Stages
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id)
            .Select(e => new StageProbe(e.LeagueId, e.IsComplete))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<List<RoundProbe>> LoadRoundsAsync(SaveDbContext context, SeasonEntity season, CancellationToken cancellationToken)
    {
        return await context.Rounds
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id)
            .Select(e => new RoundProbe(e.LeagueId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<HashSet<int>> LoadFinalsAsync(SaveDbContext context, SeasonEntity season, CancellationToken cancellationToken)
    {
        return await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id)
            .Select(e => e.LeagueId)
            .Distinct()
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static ListHistoryCompetitionsResponse MapResponse(Guid saveId, SeasonEntity season, CompetitionQuery query)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(query);
        Dictionary<int, List<StageProbe>> stagesByLeague = query.Stages
            .GroupBy(e => e.LeagueId)
            .ToDictionary(g => g.Key, g => g.ToList());
        Dictionary<int, int> roundsByLeague = query.Rounds
            .GroupBy(e => e.LeagueId)
            .ToDictionary(g => g.Key, g => g.Count());
        List<HistoryCompetitionSummary> summaries = new(query.Leagues.Count);
        foreach (LeagueEntity league in query.Leagues)
        {
            summaries.Add(MapLeague(league, stagesByLeague, roundsByLeague, query.LeaguesWithFinal));
        }

        return new ListHistoryCompetitionsResponse(saveId, season.SeasonNumber, season.IsComplete, summaries);
    }

    internal static HistoryCompetitionSummary MapLeague(
        LeagueEntity league,
        Dictionary<int, List<StageProbe>> stagesByLeague,
        Dictionary<int, int> roundsByLeague,
        HashSet<int> leaguesWithFinal)
    {
        stagesByLeague.TryGetValue(league.Id, out List<StageProbe>? leagueStages);
        int stageCount = leagueStages?.Count ?? 0;
        int completedStages = leagueStages?.Count(e => e.IsComplete) ?? 0;
        roundsByLeague.TryGetValue(league.Id, out int totalRounds);
        string kind = ((LeagueKind)league.Kind).ToString();
        string colorName = ((SportingColor)league.SportingColor).ToString();
        SimulationKernel.Leagues.LeagueLevel level = LeagueEntityLevels.GetLevel(league);
        return new HistoryCompetitionSummary(
            league.Id,
            league.Name,
            kind,
            league.FeederDivision,
            level.ToString(),
            league.SportingColor,
            colorName,
            stageCount,
            completedStages,
            totalRounds,
            leaguesWithFinal.Contains(league.Id));
    }

    internal static async Task<SeasonEntity> LoadSeasonAsync(SaveDbContext context, int seasonNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SeasonEntity? season = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber, cancellationToken)
            .ConfigureAwait(false);
        if (season is null)
        {
            throw new HistoryNotFoundException($"Season {seasonNumber} does not exist.");
        }

        return season;
    }
}
