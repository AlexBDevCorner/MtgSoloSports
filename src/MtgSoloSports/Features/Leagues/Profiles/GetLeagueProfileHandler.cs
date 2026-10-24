using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Leagues.Profiles;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Serves one stable league's
/// full historical profile from persisted completed final season standings in
/// that league only. Reads normalized tables in a few grouped queries (never
/// per-season/per-athlete fan-out, never round payloads) and recalculates on
/// read; simulation rules and recorded results are untouched. Read-only: no
/// lock, no RNG access, no mutation.
/// </summary>
public sealed class GetLeagueProfileHandler
{
    private readonly SaveStore _store;

    public GetLeagueProfileHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetLeagueProfileResponse> HandleAsync(Guid saveId, string leagueKey, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (string.IsNullOrWhiteSpace(leagueKey))
        {
            throw new ArgumentException("League key must not be empty.", nameof(leagueKey));
        }

        if (!LeagueKey.TryParse(leagueKey, out int kind, out int? feederDivision, out int? sportingColor))
        {
            throw new LeagueProfileNotFoundException($"League '{leagueKey}' does not exist in this save.");
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        List<SeasonEntity> seasons = await context.Seasons
            .AsNoTracking()
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<LeagueEntity> leagues = await context.Leagues
            .AsNoTracking()
            .Where(e => e.Kind == kind
                && (kind == (int)LeagueKind.Superleague || e.FeederDivision == feederDivision)
                && (kind == (int)LeagueKind.Superleague || e.SportingColor == sportingColor))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (leagues.Count == 0)
        {
            throw new LeagueProfileNotFoundException($"League '{leagueKey}' does not exist in this save.");
        }

        HashSet<int> leagueIds = leagues.Select(l => l.Id).ToHashSet();
        List<SeasonStandingProbe> standings = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => leagueIds.Contains(e.LeagueId))
            .Select(e => new SeasonStandingProbe(
                e.SeasonId, e.LeagueId, e.SaveAthleteId, e.SeasonRank,
                e.TotalChampionshipPointsThousandths, e.IsChampion))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        return Map(saveId, leagueKey, seasons, leagues, standings, names);
    }

    internal sealed record SeasonStandingProbe(
        int SeasonId,
        int LeagueId,
        int SaveAthleteId,
        int SeasonRank,
        int TotalChampionshipPointsThousandths,
        bool IsChampion);

    internal static GetLeagueProfileResponse Map(
        Guid saveId,
        string leagueKey,
        List<SeasonEntity> seasons,
        List<LeagueEntity> leagues,
        List<SeasonStandingProbe> standings,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        ArgumentNullException.ThrowIfNull(leagues);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(names);
        if (leagues.Count == 0)
        {
            throw new LeagueProfileNotFoundException($"League '{leagueKey}' does not exist in this save.");
        }

        Dictionary<int, SeasonEntity> seasonsById = seasons.ToDictionary(s => s.Id);
        List<LeagueProfileAggregator.LeagueSeasonInput> inputs = BuildInputs(leagues, seasonsById, standings);
        LeagueEntity latest = LatestLeague(leagues, seasonsById);
        return Assemble(saveId, inputs, latest, names);
    }

    internal static List<LeagueProfileAggregator.LeagueSeasonInput> BuildInputs(
        List<LeagueEntity> leagues,
        Dictionary<int, SeasonEntity> seasonsById,
        List<SeasonStandingProbe> standings)
    {
        ArgumentNullException.ThrowIfNull(leagues);
        ArgumentNullException.ThrowIfNull(seasonsById);
        ArgumentNullException.ThrowIfNull(standings);
        Dictionary<(int SeasonId, int LeagueId), List<SeasonStandingProbe>> rowsBySeasonLeague = standings
            .GroupBy(s => (s.SeasonId, s.LeagueId))
            .ToDictionary(g => g.Key, g => g.ToList());
        List<LeagueProfileAggregator.LeagueSeasonInput> inputs = new(leagues.Count);
        foreach (LeagueEntity league in leagues)
        {
            if (!seasonsById.TryGetValue(league.SeasonId, out SeasonEntity? season))
            {
                throw new InvalidOperationException($"League '{league.Name}' references unknown season {league.SeasonId}.");
            }

            rowsBySeasonLeague.TryGetValue((league.SeasonId, league.Id), out List<SeasonStandingProbe>? rows);
            inputs.Add(new LeagueProfileAggregator.LeagueSeasonInput(
                season.SeasonNumber,
                season.Id,
                league.Id,
                league.Kind,
                league.FeederDivision,
                league.SportingColor,
                league.Name,
                season.IsComplete,
                MapRows(rows)));
        }

        return inputs;
    }

    internal static List<LeagueProfileAggregator.LeagueStandingInput> MapRows(List<SeasonStandingProbe>? rows)
    {
        if (rows is null)
        {
            return [];
        }

        return rows
            .OrderBy(r => r.SeasonRank)
            .Select(r => new LeagueProfileAggregator.LeagueStandingInput(
                r.SaveAthleteId, r.SeasonRank, r.TotalChampionshipPointsThousandths, r.IsChampion))
            .ToList();
    }

    internal static LeagueEntity LatestLeague(List<LeagueEntity> leagues, Dictionary<int, SeasonEntity> seasonsById)
    {
        ArgumentNullException.ThrowIfNull(leagues);
        ArgumentNullException.ThrowIfNull(seasonsById);
        return leagues
            .OrderByDescending(l => seasonsById.TryGetValue(l.SeasonId, out SeasonEntity? s) ? s.SeasonNumber : -1)
            .ThenByDescending(l => l.Id)
            .First();
    }

    internal static GetLeagueProfileResponse Assemble(
        Guid saveId,
        List<LeagueProfileAggregator.LeagueSeasonInput> inputs,
        LeagueEntity latest,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(names);
        List<LeagueProfileAggregator.LeagueSeasonInput> completed = inputs
            .Where(i => i.IsComplete && i.Standings.Count > 0)
            .ToList();
        HashSet<int> athletes = new();
        foreach (LeagueProfileAggregator.LeagueSeasonInput input in completed)
        {
            foreach (LeagueProfileAggregator.LeagueStandingInput row in input.Standings)
            {
                athletes.Add(row.SaveAthleteId);
            }
        }

        int? firstSeason = inputs.Count > 0 ? inputs.Min(i => i.SeasonNumber) : null;
        int? lastCompleted = completed.Count > 0 ? completed.Max(i => i.SeasonNumber) : null;
        CurrentSeasonInfo? currentInfo = MapCurrent(inputs);

        string kind = ListLeagueProfilesHandler.ResolveKind(latest);
        return new GetLeagueProfileResponse(
            saveId,
            LeagueKey.Build(latest.Kind, latest.FeederDivision, latest.SportingColor),
            latest.Name,
            kind,
            latest.FeederDivision,
            ListLeagueProfilesHandler.ResolveLevel(latest),
            latest.SportingColor,
            ListLeagueProfilesHandler.ResolveColorName(latest),
            inputs.Select(i => i.SeasonNumber).Distinct().Count(),
            completed.Select(i => i.SeasonNumber).Distinct().Count(),
            athletes.Count,
            firstSeason,
            lastCompleted,
            currentInfo,
            LeagueProfileAggregator.BuildPodiumHistory(inputs, names),
            LeagueProfileAggregator.BuildLeaderboard(inputs, names),
            LeagueProfileAggregator.BuildRecords(inputs, names));
    }

    internal static CurrentSeasonInfo? MapCurrent(List<LeagueProfileAggregator.LeagueSeasonInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        LeagueProfileAggregator.LeagueSeasonInput? current = inputs
            .Where(i => !(i.IsComplete && i.Standings.Count > 0))
            .OrderByDescending(i => i.SeasonNumber)
            .FirstOrDefault();
        return current is null
            ? null
            : new CurrentSeasonInfo(current.SeasonNumber, current.LeagueId, current.IsComplete, current.Standings.Count > 0);
    }
}
