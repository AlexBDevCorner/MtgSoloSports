using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Features.Leagues.CurrentStandings;
using MtgSoloSports.Features.Simulation.AdvanceRound;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Seasons;

namespace MtgSoloSports.Features.Leagues.SeasonTable;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Reads a completed league
/// season table from persisted final standings. Throws
/// <see cref="SeasonTableNotFoundException"/> (404) when the season is not yet
/// finalized so callers can distinguish "not yet complete" from bad requests.
/// </summary>
public sealed class GetSeasonTableHandler
{
    private readonly SaveStore _store;

    public GetSeasonTableHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<GetSeasonTableResponse> HandleAsync(Guid saveId, int leagueId, int seasonNumber, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (leagueId <= 0)
        {
            throw new ArgumentException("League id must be positive.", nameof(leagueId));
        }

        if (seasonNumber < 1)
        {
            throw new ArgumentException("Season number must be positive.", nameof(seasonNumber));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        SeasonEntity season = await LoadSeasonAsync(context, seasonNumber, cancellationToken).ConfigureAwait(false);
        LeagueEntity league = await GetCurrentStandingsHandler.LoadLeagueAsync(context, season, leagueId, cancellationToken).ConfigureAwait(false);
        return await BuildResponseAsync(context, saveId, season, league, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SeasonEntity> LoadSeasonAsync(
        SaveDbContext context,
        int seasonNumber,
        CancellationToken cancellationToken)
    {
        SeasonEntity? season = await context.Seasons
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeasonNumber == seasonNumber, cancellationToken)
            .ConfigureAwait(false);
        if (season is null)
        {
            throw new SeasonTableNotFoundException($"Season {seasonNumber} does not exist.");
        }

        return season;
    }

    internal static async Task<GetSeasonTableResponse> BuildResponseAsync(
        SaveDbContext context,
        Guid saveId,
        SeasonEntity season,
        LeagueEntity league,
        CancellationToken cancellationToken)
    {
        if (!season.IsComplete)
        {
            throw new SeasonTableNotFoundException($"Season {season.SeasonNumber} for league '{league.Name}' is not yet complete.");
        }

        List<SeasonStandingEntity> rows = await context.SeasonStandings
            .AsNoTracking()
            .Where(e => e.SeasonId == season.Id && e.LeagueId == league.Id)
            .OrderBy(e => e.SeasonRank)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Count == 0)
        {
            throw new SeasonTableNotFoundException($"Season {season.SeasonNumber} for league '{league.Name}' has no final table.");
        }

        Dictionary<int, string> names = await GetCurrentStandingsHandler.LoadNamesAsync(context, cancellationToken).ConfigureAwait(false);
        List<SeasonTableEntry> standings = MapEntries(rows, names);
        string checksum = ComputeChecksum(rows, names);
        return new GetSeasonTableResponse(
            saveId,
            season.SeasonNumber,
            league.Id,
            league.Name,
            true,
            checksum,
            standings);
    }

    internal static List<SeasonTableEntry> MapEntries(List<SeasonStandingEntity> rows, Dictionary<int, string> names)
    {
        List<SeasonTableEntry> standings = new(rows.Count);
        foreach (SeasonStandingEntity row in rows)
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            standings.Add(new SeasonTableEntry(
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

    internal static string ComputeChecksum(List<SeasonStandingEntity> rows, Dictionary<int, string> names)
    {
        List<SeasonRankedAthlete> ranked = new(rows.Count);
        foreach (SeasonStandingEntity row in rows.OrderBy(r => r.SeasonRank))
        {
            names.TryGetValue(row.SaveAthleteId, out string? name);
            List<int> stageCounts = JsonSerializer.Deserialize<List<int>>(row.StagePlaceCountsJson) ?? [];
            List<int> roundCounts = JsonSerializer.Deserialize<List<int>>(row.RoundPlaceCountsJson) ?? [];
            ranked.Add(new SeasonRankedAthlete(
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}",
                row.SeasonRank,
                row.TotalChampionshipPointsThousandths,
                row.TotalStageScoreThousandths,
                row.TotalBaseScoreThousandths,
                row.StageWins,
                row.RoundWins,
                stageCounts,
                roundCounts,
                row.IsChampion));
        }

        return SeasonCalculator.ComputeChecksum(ranked);
    }
}
