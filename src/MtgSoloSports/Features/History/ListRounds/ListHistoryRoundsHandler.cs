using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.History.ListStages;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.ListRounds;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Lists round summaries for
/// one historical stage using normalized round identity only. The query
/// projects <c>RoundNumber</c>, <c>RulesVersion</c> and
/// <c>PayloadChecksum</c> and never selects <c>PayloadJson</c>, so no payload
/// is transferred or decompressed here. Only the exact-round replay handler
/// decompresses a single payload.
/// Read-only: no lock, no RNG access, no mutation.
/// </summary>
public sealed class ListHistoryRoundsHandler
{
    private readonly SaveStore _store;

    public ListHistoryRoundsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListHistoryRoundsResponse> HandleAsync(Guid saveId, int seasonNumber, int leagueId, int stageNumber, CancellationToken cancellationToken = default)
    {
        ValidateIds(saveId, seasonNumber, leagueId, stageNumber);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity season = await ListHistoryCompetitionsHandler.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await ListHistoryStagesHandler.LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);
        RoundQuery query = await LoadQueryAsync(context, season, league, stageNumber, cancellationToken).ConfigureAwait(false);
        return MapResponse(saveId, season, league, stageNumber, query);
    }

    internal static void ValidateIds(Guid saveId, int seasonNumber, int leagueId, int stageNumber)
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

        if (stageNumber < 1)
        {
            throw new ArgumentException("Stage number must be positive.", nameof(stageNumber));
        }

        if (stageNumber > 32)
        {
            throw new ArgumentException("Stage number is out of range.", nameof(stageNumber));
        }
    }

    internal sealed record RoundQuery(
        int RoundsPerStage,
        StageEntity? Stage,
        List<HistoryRoundSummary> Rounds);

    internal static async Task<RoundQuery> LoadQueryAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, int stageNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        int roundsPerStage = await ListHistoryStagesHandler.LoadRoundsPerStageAsync(context, cancellationToken).ConfigureAwait(false);
        StageEntity? stage = await LoadStageAsync(context, season, league, stageNumber, cancellationToken).ConfigureAwait(false);
        List<HistoryRoundSummary> rounds = await LoadSummariesAsync(context, season, league, stageNumber, cancellationToken).ConfigureAwait(false);
        return new RoundQuery(roundsPerStage, stage, rounds);
    }

    internal static async Task<StageEntity?> LoadStageAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, int stageNumber, CancellationToken cancellationToken)
    {
        return await context.Stages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.StageNumber == stageNumber,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<List<HistoryRoundSummary>> LoadSummariesAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, int stageNumber, CancellationToken cancellationToken)
    {
        return await context.Rounds
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.StageNumber == stageNumber)
            .OrderBy(e => e.RoundNumber)
            .Select(e => new HistoryRoundSummary(e.RoundNumber, e.RulesVersion, e.PayloadChecksum))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static ListHistoryRoundsResponse MapResponse(Guid saveId, SeasonEntity season, LeagueEntity league, int stageNumber, RoundQuery query)
    {
        ArgumentNullException.ThrowIfNull(season);
        ArgumentNullException.ThrowIfNull(league);
        ArgumentNullException.ThrowIfNull(query);
        ValidateSummaries(query.Rounds, league, stageNumber, query.RoundsPerStage);
        ValidateCursor(query.Stage, query.Rounds, league, stageNumber, query.RoundsPerStage);
        bool isComplete = query.Stage?.IsComplete ?? false;
        return new ListHistoryRoundsResponse(
            saveId,
            season.SeasonNumber,
            league.Id,
            league.Name,
            stageNumber,
            query.Rounds.Count,
            query.RoundsPerStage,
            isComplete,
            query.Rounds);
    }

    internal static void ValidateCursor(StageEntity? stage, List<HistoryRoundSummary> rounds, LeagueEntity league, int stageNumber, int roundsPerStage)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(league);
        if (stage is not null)
        {
            ListHistoryStagesHandler.ValidateStage(stage, league, roundsPerStage);
            if (stage.CompletedRounds != rounds.Count)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {stageNumber} cursor says {stage.CompletedRounds} rounds but {rounds.Count} round rows exist.");
            }
        }
        else if (rounds.Count != 0)
        {
            throw new InvalidOperationException($"League '{league.Name}' stage {stageNumber} has rounds without a stage cursor.");
        }
    }

    internal static void ValidateSummaries(List<HistoryRoundSummary> rounds, LeagueEntity league, int stageNumber, int roundsPerStage)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(league);
        if (rounds.Count > roundsPerStage)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' stage {stageNumber} has {rounds.Count} persisted rounds, more than {roundsPerStage}.");
        }

        for (int i = 0; i < rounds.Count; i++)
        {
            if (rounds[i].RoundNumber != i + 1)
            {
                throw new InvalidOperationException(
                    $"League '{league.Name}' stage {stageNumber} has non-sequential round rows; rounds must advance in order.");
            }

            if (string.IsNullOrWhiteSpace(rounds[i].PayloadChecksum))
            {
                throw new InvalidOperationException($"Persisted round {rounds[i].RoundNumber} has corrupt checksum.");
            }
        }
    }
}
