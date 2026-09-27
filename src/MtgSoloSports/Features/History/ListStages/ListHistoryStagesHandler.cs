using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.ListStages;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Lists stages for one
/// historical competition from normalized tables only. Never selects
/// <c>Rounds.PayloadJson</c> and never decompresses round payloads.
/// Read-only: no lock, no RNG access, no mutation.
/// </summary>
public sealed class ListHistoryStagesHandler
{
    private readonly SaveStore _store;

    public ListHistoryStagesHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListHistoryStagesResponse> HandleAsync(Guid saveId, int seasonNumber, int leagueId, CancellationToken cancellationToken = default)
    {
        ValidateIds(saveId, seasonNumber, leagueId);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity season = await ListHistoryCompetitionsHandler.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);
        StageQuery query = await LoadQueryAsync(context, season, league, cancellationToken).ConfigureAwait(false);
        return MapResponse(saveId, season, league, query);
    }

    internal static void ValidateIds(Guid saveId, int seasonNumber, int leagueId)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(seasonNumber));
        }

        if (leagueId <= 0)
        {
            throw new ArgumentException("League id must be positive.", nameof(leagueId));
        }
    }

    internal sealed record StageQuery(
        int RoundsPerStage,
        List<StageEntity> Stages,
        List<RoundProbe> Rounds,
        HashSet<int> StagesWithStandings);

    internal sealed record RoundProbe(int StageNumber);

    internal static async Task<StageQuery> LoadQueryAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        int roundsPerStage = await LoadRoundsPerStageAsync(context, cancellationToken).ConfigureAwait(false);
        List<StageEntity> stages = await LoadStagesAsync(context, season, league, cancellationToken).ConfigureAwait(false);
        List<RoundProbe> rounds = await LoadRoundsAsync(context, season, league, cancellationToken).ConfigureAwait(false);
        HashSet<int> standings = await LoadStandingsAsync(context, season, league, cancellationToken).ConfigureAwait(false);
        return new StageQuery(roundsPerStage, stages, rounds, standings);
    }

    internal static async Task<List<StageEntity>> LoadStagesAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, CancellationToken cancellationToken)
    {
        return await context.Stages
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.StageNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<List<RoundProbe>> LoadRoundsAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, CancellationToken cancellationToken)
    {
        return await context.Rounds
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .Select(e => new RoundProbe(e.StageNumber))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<HashSet<int>> LoadStandingsAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, CancellationToken cancellationToken)
    {
        return await context.StageStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .Select(e => e.StageNumber)
            .Distinct()
            .ToHashSetAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static ListHistoryStagesResponse MapResponse(Guid saveId, SeasonEntity season, LeagueEntity league, StageQuery query)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(query);
        Dictionary<int, int> roundsByStage = query.Rounds
            .GroupBy(e => e.StageNumber)
            .ToDictionary(g => g.Key, g => g.Count());
        List<HistoryStageSummary> summaries = new(query.Stages.Count);
        foreach (StageEntity stage in query.Stages)
        {
            summaries.Add(MapStage(stage, league, query, roundsByStage));
        }

        return new ListHistoryStagesResponse(saveId, season.SeasonNumber, league.Id, league.Name, summaries);
    }

    internal static HistoryStageSummary MapStage(StageEntity stage, LeagueEntity league, StageQuery query, Dictionary<int, int> roundsByStage)
    {
        ValidateStage(stage, league, query.RoundsPerStage);
        roundsByStage.TryGetValue(stage.StageNumber, out int roundCount);
        if (roundCount != stage.CompletedRounds)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' stage {stage.StageNumber} cursor says {stage.CompletedRounds} rounds but {roundCount} round rows exist.");
        }

        return new HistoryStageSummary(
            stage.StageNumber,
            stage.CompletedRounds,
            query.RoundsPerStage,
            stage.IsComplete,
            query.StagesWithStandings.Contains(stage.StageNumber),
            roundCount);
    }

    internal static async Task<LeagueEntity> LoadLeagueAsync(SaveDbContext context, SeasonEntity season, int leagueId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        LeagueEntity? league = await context.Leagues
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == leagueId, cancellationToken)
            .ConfigureAwait(false);
        if (league is null || league.SeasonId != season.Id)
        {
            throw new HistoryNotFoundException($"League {leagueId} is not part of season {season.SeasonNumber}.");
        }

        return league;
    }

    internal static async Task<int> LoadRoundsPerStageAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return (await AdvanceRoundHandler.LoadRulesAsync(context, cancellationToken).ConfigureAwait(false)).RoundsPerStage;
    }

    internal static void ValidateStage(StageEntity stage, LeagueEntity league, int roundsPerStage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(league);
        if (stage.StageNumber < 1)
        {
            throw new InvalidOperationException($"League '{league.Name}' has corrupt stage {stage.StageNumber}.");
        }

        if (stage.CompletedRounds < 0 || stage.CompletedRounds > roundsPerStage)
        {
            throw new InvalidOperationException($"League '{league.Name}' stage {stage.StageNumber} has corrupt completed-round count {stage.CompletedRounds}.");
        }

        if (stage.IsComplete && stage.CompletedRounds != roundsPerStage)
        {
            throw new InvalidOperationException($"League '{league.Name}' stage {stage.StageNumber} is marked complete with {stage.CompletedRounds} rounds.");
        }
    }
}
