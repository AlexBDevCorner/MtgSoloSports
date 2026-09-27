using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.History.GetStageStandings;
using MtgSoloSports.Features.History.ListCompetitions;
using MtgSoloSports.Features.History.ListStages;
using MtgSoloSports.Features.Leagues.CurrentStandings;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.History.GetSeasonTable;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads one historical
/// season's final table from persisted <c>SeasonStanding</c> rows only. Never
/// selects round payloads and never decompresses history. Throws
/// <see cref="HistoryNotFoundException"/> (404) when the season is not yet
/// finalized. Read-only: no lock, no RNG access, no mutation.
/// </summary>
public sealed class GetHistorySeasonTableHandler
{
    private readonly SaveStore _store;

    public GetHistorySeasonTableHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetHistorySeasonTableResponse> HandleAsync(Guid saveId, int seasonNumber, int leagueId, CancellationToken cancellationToken = default)
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

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity season = await ListHistoryCompetitionsHandler.LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await ListHistoryStagesHandler.LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);

        if (!season.IsComplete)
        {
            throw new HistoryNotFoundException($"Season {season.SeasonNumber} for league '{league.Name}' is not yet complete.");
        }

        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.SeasonRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new HistoryNotFoundException($"Season {season.SeasonNumber} for league '{league.Name}' has no final table.");
        }

        ValidateRows(rows, league);
        Dictionary<int, string> names = await GetHistoryStageStandingsHandler.LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        List<HistorySeasonTableEntry> standings = MapEntries(rows, names);
        string checksum = GetCurrentStandingsHandler.ComputePersistedChecksum(rows, names);
        return new GetHistorySeasonTableResponse(
            saveId,
            season.SeasonNumber,
            league.Id,
            league.Name,
            true,
            checksum,
            standings);
    }

    internal static void ValidateRows(List<SeasonStandingEntity> rows, LeagueEntity league)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(league);
        if (rows.Count != 32)
        {
            throw new InvalidOperationException(
                $"League '{league.Name}' must hold exactly 32 final standings, was {rows.Count}.");
        }

        HashSet<int> ranks = rows.Select(r => r.SeasonRank).ToHashSet();
        if (!ranks.SetEquals(Enumerable.Range(1, 32)))
        {
            throw new InvalidOperationException($"League '{league.Name}' must cover ranks 1..32 exactly once.");
        }
    }

    internal static List<HistorySeasonTableEntry> MapEntries(List<SeasonStandingEntity> rows, Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);
        List<HistorySeasonTableEntry> standings = new(rows.Count);
        foreach (SeasonStandingEntity row in rows.OrderBy(r => r.SeasonRank))
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            standings.Add(new HistorySeasonTableEntry(
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}",
                row.SeasonRank,
                row.TotalChampionshipPointsThousandths,
                row.TotalStageScoreThousandths,
                row.TotalBaseScoreThousandths,
                row.StageWins,
                row.RoundWins,
                row.IsChampion));
        }

        return standings;
    }
}
