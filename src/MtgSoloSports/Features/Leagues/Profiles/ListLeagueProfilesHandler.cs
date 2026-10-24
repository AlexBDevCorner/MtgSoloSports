using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;
using MtgSoloSports.SimulationKernel.Catalog;

namespace MtgSoloSports.Features.Leagues.Profiles;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Lists every distinct official
/// league identity in a save, grouped by the stable cross-season
/// <see cref="LeagueKey"/>. Built from normalized tables only; never reads
/// round payloads. Read-only: no lock, no RNG access, no mutation.
/// </summary>
public sealed class ListLeagueProfilesHandler
{
    private readonly SaveStore _store;

    public ListLeagueProfilesHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListLeagueProfilesResponse> HandleAsync(Guid saveId, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
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
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<StandingProbe> standings = await context.SeasonStandings
            .AsNoTracking()
            .Select(e => new StandingProbe(e.SeasonId, e.LeagueId, e.SaveAthleteId, e.SeasonRank, e.TotalChampionshipPointsThousandths))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        return Map(saveId, seasons, leagues, standings, names);
    }

    internal sealed record StandingProbe(
        int SeasonId,
        int LeagueId,
        int SaveAthleteId,
        int SeasonRank,
        int TotalChampionshipPointsThousandths);

    internal static ListLeagueProfilesResponse Map(
        Guid saveId,
        List<SeasonEntity> seasons,
        List<LeagueEntity> leagues,
        List<StandingProbe> standings,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(seasons);
        ArgumentNullException.ThrowIfNull(leagues);
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(names);
        Dictionary<int, SeasonEntity> seasonsById = seasons.ToDictionary(s => s.Id);
        Dictionary<int, List<StandingProbe>> standingsByLeague = standings
            .GroupBy(s => s.LeagueId)
            .ToDictionary(g => g.Key, g => g.ToList());
        List<ListLeagueProfilesResponse.LeagueProfileSummary> summaries = leagues
            .GroupBy(l => LeagueKey.Build(l.Kind, l.FeederDivision, l.SportingColor), StringComparer.Ordinal)
            .Select(group => MapGroup(group.Key, [.. group], seasonsById, standingsByLeague, names))
            .ToList();
        return new ListLeagueProfilesResponse(saveId, OrderSummaries(summaries));
    }

    internal static List<ListLeagueProfilesResponse.LeagueProfileSummary> OrderSummaries(
        List<ListLeagueProfilesResponse.LeagueProfileSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        return summaries
            .OrderBy(s => LeagueOrder(s.LeagueLevel))
            .ThenBy(s => s.SportingColor)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.LeagueKey, StringComparer.Ordinal)
            .ToList();
    }

    internal static ListLeagueProfilesResponse.LeagueProfileSummary MapGroup(
        string leagueKey,
        List<LeagueEntity> group,
        Dictionary<int, SeasonEntity> seasonsById,
        Dictionary<int, List<StandingProbe>> standingsByLeague,
        Dictionary<int, string> names)
    {
        LeagueEntity latest = group
            .Where(l => seasonsById.ContainsKey(l.SeasonId))
            .OrderByDescending(l => seasonsById[l.SeasonId].SeasonNumber)
            .ThenByDescending(l => l.Id)
            .First();
        List<int> seasonNumbers = group
            .Where(l => seasonsById.ContainsKey(l.SeasonId))
            .Select(l => seasonsById[l.SeasonId].SeasonNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        List<(int SeasonNumber, StandingProbe Champion)> champions = [];
        HashSet<int> athletes = new();
        int completedSeasons = AccumulateGroup(group, seasonsById, standingsByLeague, champions, athletes);

        champions.Sort((a, b) => a.SeasonNumber.CompareTo(b.SeasonNumber));
        (int SeasonNumber, StandingProbe Champion)? latestChampion = champions.Count > 0
            ? champions[champions.Count - 1]
            : null;
        return new ListLeagueProfilesResponse.LeagueProfileSummary(
            leagueKey,
            latest.Name,
            ResolveKind(latest),
            latest.FeederDivision,
            ResolveLevel(latest),
            latest.SportingColor,
            ResolveColorName(latest),
            seasonNumbers.Count,
            completedSeasons,
            athletes.Count,
            seasonNumbers.Count > 0 ? seasonNumbers[0] : null,
            latestChampion?.SeasonNumber,
            MapChampion(latestChampion, names));
    }

    internal static int AccumulateGroup(
        List<LeagueEntity> group,
        Dictionary<int, SeasonEntity> seasonsById,
        Dictionary<int, List<StandingProbe>> standingsByLeague,
        List<(int SeasonNumber, StandingProbe Champion)> champions,
        HashSet<int> athletes)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(seasonsById);
        ArgumentNullException.ThrowIfNull(standingsByLeague);
        ArgumentNullException.ThrowIfNull(champions);
        ArgumentNullException.ThrowIfNull(athletes);
        int completedSeasons = 0;
        foreach (LeagueEntity league in group)
        {
            if (!seasonsById.TryGetValue(league.SeasonId, out SeasonEntity? season)
                || !season.IsComplete)
            {
                continue;
            }

            if (!standingsByLeague.TryGetValue(league.Id, out List<StandingProbe>? rows)
                || rows.Count == 0)
            {
                continue;
            }

            completedSeasons++;
            foreach (StandingProbe row in rows)
            {
                athletes.Add(row.SaveAthleteId);
            }

            StandingProbe? champion = rows
                .OrderBy(r => r.SeasonRank)
                .FirstOrDefault(r => r.SeasonRank == 1);
            if (champion is not null)
            {
                champions.Add((season.SeasonNumber, champion));
            }
        }

        return completedSeasons;
    }

    internal static ListLeagueProfilesResponse.ChampionRef? MapChampion(
        (int SeasonNumber, StandingProbe Champion)? latestChampion,
        Dictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (latestChampion is null)
        {
            return null;
        }

        names.TryGetValue(latestChampion.Value.Champion.SaveAthleteId, out string? name);
        return new ListLeagueProfilesResponse.ChampionRef(
            latestChampion.Value.Champion.SaveAthleteId,
            name ?? $"Athlete {latestChampion.Value.Champion.SaveAthleteId}",
            latestChampion.Value.SeasonNumber,
            latestChampion.Value.Champion.TotalChampionshipPointsThousandths);
    }

    internal static string ResolveKind(LeagueEntity latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        return ((LeagueKind)latest.Kind).ToString();
    }

    internal static string ResolveColorName(LeagueEntity latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        return Enum.IsDefined(typeof(SportingColor), latest.SportingColor)
            ? ((SportingColor)latest.SportingColor).ToString()
            : "Unknown";
    }

    internal static string ResolveLevel(LeagueEntity latest)
    {
        ArgumentNullException.ThrowIfNull(latest);
        try
        {
            return LeagueEntityLevels.GetLevel(latest).ToString();
        }
        catch (InvalidOperationException)
        {
            return string.Equals(ResolveKind(latest), nameof(LeagueKind.Superleague), StringComparison.Ordinal)
                ? SimulationKernel.Leagues.LeagueLevel.Superleague.ToString()
                : SimulationKernel.Leagues.LeagueLevel.Feeder1.ToString();
        }
    }

    private static int LeagueOrder(string leagueLevel)
    {
        return leagueLevel switch
        {
            nameof(SimulationKernel.Leagues.LeagueLevel.Superleague) => 0,
            nameof(SimulationKernel.Leagues.LeagueLevel.Feeder1) => 1,
            nameof(SimulationKernel.Leagues.LeagueLevel.Feeder2) => 2,
            nameof(SimulationKernel.Leagues.LeagueLevel.Feeder3) => 3,
            _ => 4,
        };
    }
}
