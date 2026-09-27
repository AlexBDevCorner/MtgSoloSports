using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.History.ListStages;
using MtgSoloSports.Features.Simulation.CompleteStage;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Stages;

namespace MtgSoloSports.Features.History.GetStageStandings;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one historical
/// stage's standings from normalized <c>StageStanding</c> rows only. Never
/// selects round payloads and never decompresses history. Read-only: no lock,
/// no RNG access, no mutation.
/// </summary>
public sealed class GetHistoryStageStandingsHandler
{
    private readonly SaveStore _store;

    public GetHistoryStageStandingsHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetHistoryStageStandingsResponse> HandleAsync(Guid saveId, int seasonNumber, int leagueId, int stageNumber, CancellationToken cancellationToken = default)
    {
        ValidateIds(saveId, seasonNumber, leagueId, stageNumber);
        using SaveDbContext context = _store.OpenDbContext(saveId);
        StandingQuery query = await LoadQueryAsync(context, seasonNumber, leagueId, stageNumber, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, query, cancellationToken).ConfigureAwait(false);
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
    }

    internal sealed record StandingQuery(
        SeasonEntity Season,
        LeagueEntity League,
        int StageNumber,
        StageEntity? Stage,
        List<StageStandingEntity> Rows);

    internal static async Task<StandingQuery> LoadQueryAsync(SaveDbContext context, int seasonNumber, int leagueId, int stageNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SeasonEntity season = await ListHistoryCompetitionsHandler.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await ListHistoryStagesHandler.LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);
        StageEntity? stage = await LoadStageAsync(context, season, league, stageNumber, cancellationToken).ConfigureAwait(false);
        List<StageStandingEntity> rows = await LoadRowsAsync(context, season, league, stageNumber, cancellationToken).ConfigureAwait(false);
        return new StandingQuery(season, league, stageNumber, stage, rows);
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

    internal static async Task<List<StageStandingEntity>> LoadRowsAsync(SaveDbContext context, SeasonEntity season, LeagueEntity league, int stageNumber, CancellationToken cancellationToken)
    {
        return await context.StageStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id && e.StageNumber == stageNumber)
            .OrderBy(e => e.StageRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<GetHistoryStageStandingsResponse> BuildResponseAsync(SaveDbContext context, Guid saveId, StandingQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Rows.Count == 0)
        {
            return MapEmpty(saveId, query);
        }

        ValidateRows(query.Rows, query.League, query.StageNumber);
        Dictionary<int, string> names = await LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        List<HistoryStageStandingEntry> standings = MapEntries(query.Rows, names, query.League);
        string checksum = ComputeChecksum(query.Rows, names);
        return MapComplete(saveId, query, checksum, standings);
    }

    internal static GetHistoryStageStandingsResponse MapEmpty(Guid saveId, StandingQuery query)
    {
        return new GetHistoryStageStandingsResponse(
            saveId,
            query.Season.SeasonNumber,
            query.League.Id,
            query.League.Name,
            query.StageNumber,
            query.Stage?.IsComplete ?? false,
            string.Empty,
            []);
    }

    internal static GetHistoryStageStandingsResponse MapComplete(Guid saveId, StandingQuery query, string checksum, List<HistoryStageStandingEntry> standings)
    {
        return new GetHistoryStageStandingsResponse(
            saveId,
            query.Season.SeasonNumber,
            query.League.Id,
            query.League.Name,
            query.StageNumber,
            query.Stage?.IsComplete ?? true,
            checksum,
            standings);
    }

    internal static void ValidateRows(List<StageStandingEntity> rows, LeagueEntity league, int stageNumber)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(league);
        if (rows.Count != 32)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' stage {stageNumber} must hold exactly 32 standings, was {rows.Count}.");
        }

        HashSet<int> ranks = rows.Select(r => r.StageRank).ToHashSet();
        if (!ranks.SetEquals(Enumerable.Range(1, 32)))
        {
            throw new InvalidOperationException($"League '{league.Name}' stage {stageNumber} must cover ranks 1..32 exactly once.");
        }
    }

    internal static async Task<Dictionary<int, string>> LoadNamesAsync(SaveDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static List<HistoryStageStandingEntry> MapEntries(List<StageStandingEntity> rows, Dictionary<int, string> names, LeagueEntity league)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(league);
        List<HistoryStageStandingEntry> standings = new(rows.Count);
        foreach (StageStandingEntity row in rows.OrderBy(r => r.StageRank))
        {
            standings.Add(MapEntry(row, names, league));
        }

        return standings;
    }

    internal static HistoryStageStandingEntry MapEntry(StageStandingEntity row, Dictionary<int, string> names, LeagueEntity league)
    {
        if (!names.TryGetValue(row.SaveAthleteId, out string? name))
        {
            throw new InvalidOperationException($"League '{league.Name}' references unknown athlete {row.SaveAthleteId}.");
        }

        return new HistoryStageStandingEntry(
            row.SaveAthleteId,
            name,
            row.StageRank,
            row.StageScoreThousandths,
            row.BaseScoreThousandths,
            row.ChampionshipPointsThousandths,
            row.RoundWins,
            row.EarnedBonusThousandths);
    }

    internal static string ComputeChecksum(List<StageStandingEntity> rows, Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);
        List<StageRankedAthlete> ranked = new(rows.Count);
        foreach (StageStandingEntity row in rows.OrderBy(r => r.StageRank))
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            ranked.Add(new StageRankedAthlete(
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}",
                row.StageRank,
                row.StageScoreThousandths,
                row.BaseScoreThousandths,
                row.ChampionshipPointsThousandths,
                row.RoundWins,
                [],
                row.EarnedBonusThousandths));
        }

        return StageInvariants.ComputeChecksum(ranked);
    }
}
