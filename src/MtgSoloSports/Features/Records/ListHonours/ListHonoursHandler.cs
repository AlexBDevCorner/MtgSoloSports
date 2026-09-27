using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records.ListHonours;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Lists official
/// league/Superleague honours from the persisted <c>Honours</c> accelerator
/// when it matches authoritative champions, otherwise falls back to an
/// in-memory projection from <c>SeasonStandings</c> plus <c>Leagues</c> (still
/// without round payloads) so saves created before MSS-022 remain readable.
/// Read-only: no lock, no RNG access, no mutation.
/// </summary>
public sealed class ListHonoursHandler
{
    private readonly SaveStore _store;

    public ListHonoursHandler(SaveStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<ListHonoursResponse> HandleAsync(Guid saveId, int? athleteId = null, CancellationToken cancellationToken = default)
    {
        if (saveId == Guid.Empty)
        {
            throw new ArgumentException("Save id must not be empty.", nameof(saveId));
        }

        if (athleteId is <= 0)
        {
            throw new ArgumentException("Athlete id must be positive.", nameof(athleteId));
        }

        using SaveDbContext context = _store.OpenDbContext(saveId);
        await _store.EnsureMigratedAsync(saveId, cancellationToken).ConfigureAwait(false);
        List<HonourEntry> honours = await LoadHonoursAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        return new ListHonoursResponse(saveId, honours);
    }

    internal static async Task<List<HonourEntry>> LoadHonoursAsync(
        SaveDbContext context,
        int? athleteId,
        CancellationToken cancellationToken)
    {
        Dictionary<int, string> names = await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        int championCount = await context.SeasonStandings
            .CountAsync(e => e.IsChampion, cancellationToken)
            .ConfigureAwait(false);
        IQueryable<HonourEntity> honoursQuery = context.Honours.AsNoTracking();
        if (athleteId is not null)
        {
            honoursQuery = honoursQuery.Where(e => e.SaveAthleteId == athleteId.Value);
        }

        List<HonourEntity> persisted = await honoursQuery
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.LeagueName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        int leaguePersisted = persisted.Count(e =>
            e.Kind == (int)HonourKind.FeederTitle || e.Kind == (int)HonourKind.SuperleagueTitle);
        if (athleteId is null && leaguePersisted == championCount)
        {
            return MapPersisted(persisted, names);
        }

        if (athleteId is not null && persisted.Count > 0)
        {
            int expectedForAthlete = await context.SeasonStandings
                .CountAsync(e => e.IsChampion && e.SaveAthleteId == athleteId.Value, cancellationToken)
                .ConfigureAwait(false);
            int leagueForAthlete = persisted.Count(e =>
                e.Kind == (int)HonourKind.FeederTitle || e.Kind == (int)HonourKind.SuperleagueTitle);
            if (leagueForAthlete == expectedForAthlete)
            {
                return MapPersisted(persisted, names);
            }
        }

        return await LoadFallbackAsync(context, names, athleteId, persisted, cancellationToken).ConfigureAwait(false);
    }

    internal static List<HonourEntry> MapPersisted(List<HonourEntity> persisted, Dictionary<int, string> names)
    {
        List<HonourEntry> entries = new(persisted.Count);
        foreach (HonourEntity honour in persisted)
        {
            names.TryGetValue(honour.SaveAthleteId, out string? name);
            entries.Add(new HonourEntry(
                honour.SeasonNumber,
                honour.SeasonId,
                honour.LeagueId,
                honour.LeagueName,
                honour.LeagueKind,
                ((HonourKind)honour.Kind).ToString(),
                honour.SaveAthleteId,
                name ?? $"Athlete {honour.SaveAthleteId}"));
        }

        return entries;
    }

    internal static async Task<List<HonourEntry>> LoadFallbackAsync(
        SaveDbContext context,
        Dictionary<int, string> names,
        int? athleteId,
        List<HonourEntity> persistedCupHonours,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> champions = await LoadFallbackChampionsAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> seasonNumbers = await LoadFallbackSeasonNumbersAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await LoadFallbackLeaguesAsync(context, cancellationToken).ConfigureAwait(false);
        List<HonourEntry> entries = MapFallbackChampions(champions, seasonNumbers, leaguesById, names);
        AppendCupHonours(entries, persistedCupHonours, names);
        return entries
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.LeagueName, StringComparer.Ordinal)
            .ToList();
    }

    internal static async Task<List<SeasonStandingEntity>> LoadFallbackChampionsAsync(
        SaveDbContext context,
        int? athleteId,
        CancellationToken cancellationToken)
    {
        IQueryable<SeasonStandingEntity> query = context.SeasonStandings.AsNoTracking().Where(e => e.IsChampion);
        if (athleteId is not null)
        {
            query = query.Where(e => e.SaveAthleteId == athleteId.Value);
        }

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<Dictionary<int, int>> LoadFallbackSeasonNumbersAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        List<SeasonEntity> seasons = await context.Seasons
            .AsNoTracking()
            .OrderBy(e => e.SeasonNumber)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return seasons.ToDictionary(s => s.Id, s => s.SeasonNumber);
    }

    internal static async Task<Dictionary<int, LeagueEntity>> LoadFallbackLeaguesAsync(
        SaveDbContext context,
        CancellationToken cancellationToken)
    {
        return await context.Leagues
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static List<HonourEntry> MapFallbackChampions(
        List<SeasonStandingEntity> champions,
        Dictionary<int, int> seasonNumbers,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, string> names)
    {
        List<HonourEntry> entries = new(champions.Count);
        foreach (SeasonStandingEntity champion in champions)
        {
            entries.Add(MapSingleFallbackChampion(champion, seasonNumbers, leaguesById, names));
        }

        return entries;
    }

    internal static HonourEntry MapSingleFallbackChampion(
        SeasonStandingEntity champion,
        Dictionary<int, int> seasonNumbers,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, string> names)
    {
        if (!seasonNumbers.TryGetValue(champion.SeasonId, out int seasonNumber))
        {
            throw new InvalidOperationException($"Season standing {champion.Id} references unknown season {champion.SeasonId}.");
        }

        if (!leaguesById.TryGetValue(champion.LeagueId, out LeagueEntity? league))
        {
            throw new InvalidOperationException($"Season standing {champion.Id} references unknown league {champion.LeagueId}.");
        }

        HonourKind kind = HonourKindMapper.FromLeagueKind(league.Kind);
        names.TryGetValue(champion.SaveAthleteId, out string? name);
        return new HonourEntry(
            seasonNumber,
            champion.SeasonId,
            league.Id,
            league.Name,
            league.Kind,
            kind.ToString(),
            champion.SaveAthleteId,
            name ?? $"Athlete {champion.SaveAthleteId}");
    }

    internal static void AppendCupHonours(
        List<HonourEntry> entries,
        List<HonourEntity> persistedCupHonours,
        Dictionary<int, string> names)
    {
        foreach (HonourEntity cup in persistedCupHonours.Where(e =>
            e.Kind != (int)HonourKind.FeederTitle && e.Kind != (int)HonourKind.SuperleagueTitle))
        {
            names.TryGetValue(cup.SaveAthleteId, out string? name);
            entries.Add(new HonourEntry(
                cup.SeasonNumber,
                cup.SeasonId,
                cup.LeagueId,
                cup.LeagueName,
                cup.LeagueKind,
                ((HonourKind)cup.Kind).ToString(),
                cup.SaveAthleteId,
                name ?? $"Athlete {cup.SaveAthleteId}"));
        }
    }
}
