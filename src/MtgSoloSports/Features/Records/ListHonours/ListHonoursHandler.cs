using Microsoft.EntityFrameworkCore;
using MtgSoloSports.Persistence.Saves;

namespace MtgSoloSports.Features.Records.ListHonours;

/// <summary>
/// Endpoint -&gt; Handler direct call (no mediator). Lists official
/// podium honours (1st/2nd/3rd, MSS-047) from the persisted <c>Honours</c> accelerator
/// when it matches authoritative podiums, otherwise falls back to an
/// in-memory projection from <c>SeasonStandings</c> plus <c>Leagues</c> and Cup
/// standings tables (still without round payloads) so saves created before
/// MSS-022/MSS-047 remain readable. Read-only: no lock, no RNG access, no mutation.
/// Exactly one honour per qualifying podium result; never double-counts the same
/// persisted result through multiple projections.
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
        int leaguePersisted = persisted.Count(e => HonourKindMapper.IsLeagueHonourKind(e.Kind));
        int cupPersisted = persisted.Count - leaguePersisted;

        int expectedLeague = await CountExpectedLeaguePodiumsAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        int expectedCups = await CountExpectedCupPodiumsAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        if (leaguePersisted > expectedLeague || cupPersisted > expectedCups)
        {
            throw new InvalidOperationException($"Persisted honours exceed authoritative podiums; sporting state is corrupt.");
        }

        if (leaguePersisted == expectedLeague && cupPersisted == expectedCups)
        {
            return MapPersisted(persisted, names);
        }

        return await LoadFallbackAsync(context, names, athleteId, cancellationToken).ConfigureAwait(false);
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

    internal static async Task<int> CountExpectedLeaguePodiumsAsync(
        SaveDbContext context,
        int? athleteId,
        CancellationToken cancellationToken)
    {
        IQueryable<SeasonStandingEntity> query = context.SeasonStandings.AsNoTracking()
            .Where(e => e.SeasonRank >= 1 && e.SeasonRank <= 3);
        if (athleteId is not null)
        {
            query = query.Where(e => e.SaveAthleteId == athleteId.Value);
        }

        return await query.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<int> CountExpectedCupPodiumsAsync(
        SaveDbContext context,
        int? athleteId,
        CancellationToken cancellationToken)
    {
        List<HonourEntry> derived = await DeriveCupPodiumsAsync(context, athleteId, [], cancellationToken).ConfigureAwait(false);
        return derived.Count;
    }

    internal static async Task<List<HonourEntry>> LoadFallbackAsync(
        SaveDbContext context,
        Dictionary<int, string> names,
        int? athleteId,
        CancellationToken cancellationToken)
    {
        List<SeasonStandingEntity> podiums = await LoadFallbackPodiumsAsync(context, athleteId, cancellationToken).ConfigureAwait(false);
        Dictionary<int, int> seasonNumbers = await LoadFallbackSeasonNumbersAsync(context, cancellationToken).ConfigureAwait(false);
        Dictionary<int, LeagueEntity> leaguesById = await LoadFallbackLeaguesAsync(context, cancellationToken).ConfigureAwait(false);
        List<HonourEntry> entries = MapFallbackPodiums(podiums, seasonNumbers, leaguesById, names);
        List<HonourEntry> cupEntries = await DeriveCupPodiumsAsync(context, athleteId, names, cancellationToken).ConfigureAwait(false);
        entries.AddRange(cupEntries);
        return entries
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.LeagueName, StringComparer.Ordinal)
            .ToList();
    }

    internal static async Task<List<SeasonStandingEntity>> LoadFallbackPodiumsAsync(
        SaveDbContext context,
        int? athleteId,
        CancellationToken cancellationToken)
    {
        IQueryable<SeasonStandingEntity> query = context.SeasonStandings.AsNoTracking()
            .Where(e => e.SeasonRank >= 1 && e.SeasonRank <= 3);
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

    internal static List<HonourEntry> MapFallbackPodiums(
        List<SeasonStandingEntity> podiums,
        Dictionary<int, int> seasonNumbers,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, string> names)
    {
        List<HonourEntry> entries = new(podiums.Count);
        foreach (SeasonStandingEntity podium in podiums)
        {
            entries.Add(MapSingleFallbackPodium(podium, seasonNumbers, leaguesById, names));
        }

        return entries;
    }

    internal static HonourEntry MapSingleFallbackPodium(
        SeasonStandingEntity podium,
        Dictionary<int, int> seasonNumbers,
        Dictionary<int, LeagueEntity> leaguesById,
        Dictionary<int, string> names)
    {
        if (!seasonNumbers.TryGetValue(podium.SeasonId, out int seasonNumber))
        {
            throw new InvalidOperationException($"Season standing {podium.Id} references unknown season {podium.SeasonId}.");
        }

        if (!leaguesById.TryGetValue(podium.LeagueId, out LeagueEntity? league))
        {
            throw new InvalidOperationException($"Season standing {podium.Id} references unknown league {podium.LeagueId}.");
        }

        HonourKind kind = HonourKindMapper.FromLeagueRank(league.Kind, podium.SeasonRank);
        names.TryGetValue(podium.SaveAthleteId, out string? name);
        return new HonourEntry(
            seasonNumber,
            podium.SeasonId,
            league.Id,
            league.Name,
            league.Kind,
            kind.ToString(),
            podium.SaveAthleteId,
            name ?? $"Athlete {podium.SaveAthleteId}");
    }

    /// <summary>
    /// Derives cup podium honours from authoritative cup standings tables.
    /// One honour per qualifying result: individual ranks 1..3 each yield one
    /// honour; team ranks 1..min(3, N) each yield four honours (one per leg).
    /// Rank 4 or lower yields none. Never touches round payloads.
    /// Cup LeagueId/Name/Kind constants mirror the Cup slices to avoid
    /// cross-slice dependencies (0/Color Cup/-1, -1/Color Cup Team/-2,
    /// -3/Type Cup Team/-4).
    /// </summary>
    internal static async Task<List<HonourEntry>> DeriveCupPodiumsAsync(
        SaveDbContext context,
        int? athleteId,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<HonourEntry> entries = [];
        entries.AddRange(await DeriveIndividualPodiumsAsync(context, athleteId, names, cancellationToken).ConfigureAwait(false));
        entries.AddRange(await DeriveColorTeamPodiumsAsync(context, athleteId, names, cancellationToken).ConfigureAwait(false));
        entries.AddRange(await DeriveTypeTeamPodiumsAsync(context, athleteId, names, cancellationToken).ConfigureAwait(false));
        return entries;
    }

    internal static async Task<List<HonourEntry>> DeriveIndividualPodiumsAsync(
        SaveDbContext context,
        int? athleteId,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        IQueryable<ColorCupIndividualStandingEntity> query = context.ColorCupIndividualStandings
            .AsNoTracking()
            .Where(e => e.CupRank >= 1 && e.CupRank <= 3);
        if (athleteId is not null)
        {
            query = query.Where(e => e.SaveAthleteId == athleteId.Value);
        }

        List<ColorCupIndividualStandingEntity> rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<int, string> resolvedNames = names.Count > 0 ? names : await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntry> entries = new(rows.Count);
        foreach (ColorCupIndividualStandingEntity row in rows)
        {
            HonourKind kind = HonourKindMapper.FromColorCupIndividualRank(row.CupRank);
            resolvedNames.TryGetValue(row.SaveAthleteId, out string? name);
            entries.Add(new HonourEntry(
                row.SourceSeasonNumber,
                row.SourceSeasonId,
                0,
                "Color Cup",
                -1,
                kind.ToString(),
                row.SaveAthleteId,
                name ?? $"Athlete {row.SaveAthleteId}"));
        }

        return entries;
    }

    internal static async Task<List<HonourEntry>> DeriveColorTeamPodiumsAsync(
        SaveDbContext context,
        int? athleteId,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<ColorCupTeamStandingEntity> teams = await context.ColorCupTeamStandings
            .AsNoTracking()
            .Where(e => e.TeamRank >= 1 && e.TeamRank <= 3)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (teams.Count == 0)
        {
            return [];
        }

        HashSet<int> seasonIds = teams.Select(t => t.SourceSeasonId).ToHashSet();
        List<ColorCupTeamGroupStandingEntity> legs = await context.ColorCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SourceSeasonId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> resolvedNames = names.Count > 0 ? names : await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntry> entries = [];
        foreach (ColorCupTeamStandingEntity team in teams.OrderBy(t => t.SourceSeasonNumber).ThenBy(t => t.TeamRank))
        {
            HonourKind kind = HonourKindMapper.FromColorCupTeamRank(team.TeamRank);
            List<ColorCupTeamGroupStandingEntity> members = legs
                .Where(l => l.SourceSeasonId == team.SourceSeasonId && l.SportingColor == team.SportingColor)
                .ToList();
            if (members.Count != 4)
            {
                throw new InvalidOperationException($"Color Cup team rank {team.TeamRank} must field exactly four legs.");
            }

            foreach (ColorCupTeamGroupStandingEntity leg in members.OrderBy(l => l.SaveAthleteId))
            {
                if (athleteId is not null && leg.SaveAthleteId != athleteId.Value)
                {
                    continue;
                }

                resolvedNames.TryGetValue(leg.SaveAthleteId, out string? name);
                entries.Add(new HonourEntry(
                    team.SourceSeasonNumber,
                    team.SourceSeasonId,
                    -1,
                    "Color Cup Team",
                    -2,
                    kind.ToString(),
                    leg.SaveAthleteId,
                    name ?? $"Athlete {leg.SaveAthleteId}"));
            }
        }

        return entries;
    }

    internal static async Task<List<HonourEntry>> DeriveTypeTeamPodiumsAsync(
        SaveDbContext context,
        int? athleteId,
        Dictionary<int, string> names,
        CancellationToken cancellationToken)
    {
        List<TypeCupTeamStandingEntity> teams = await context.TypeCupTeamStandings
            .AsNoTracking()
            .Where(e => e.TeamRank >= 1 && e.TeamRank <= 3)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (teams.Count == 0)
        {
            return [];
        }

        HashSet<int> seasonIds = teams.Select(t => t.SourceSeasonId).ToHashSet();
        List<TypeCupTeamGroupStandingEntity> legs = await context.TypeCupTeamGroupStandings
            .AsNoTracking()
            .Where(e => seasonIds.Contains(e.SourceSeasonId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<int, string> resolvedNames = names.Count > 0 ? names : await context.SaveAthletes
            .AsNoTracking()
            .ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken)
            .ConfigureAwait(false);
        List<HonourEntry> entries = [];
        foreach (TypeCupTeamStandingEntity team in teams.OrderBy(t => t.SourceSeasonNumber).ThenBy(t => t.TeamRank))
        {
            HonourKind kind = HonourKindMapper.FromTypeCupTeamRank(team.TeamRank);
            List<TypeCupTeamGroupStandingEntity> members = legs
                .Where(l => l.SourceSeasonId == team.SourceSeasonId && string.Equals(l.CreatureType, team.CreatureType, StringComparison.Ordinal))
                .ToList();
            if (members.Count != 4)
            {
                throw new InvalidOperationException($"Type Cup team rank {team.TeamRank} must field exactly four legs.");
            }

            foreach (TypeCupTeamGroupStandingEntity leg in members.OrderBy(l => l.SaveAthleteId))
            {
                if (athleteId is not null && leg.SaveAthleteId != athleteId.Value)
                {
                    continue;
                }

                resolvedNames.TryGetValue(leg.SaveAthleteId, out string? name);
                entries.Add(new HonourEntry(
                    team.SourceSeasonNumber,
                    team.SourceSeasonId,
                    -3,
                    "Type Cup Team",
                    -4,
                    kind.ToString(),
                    leg.SaveAthleteId,
                    name ?? $"Athlete {leg.SaveAthleteId}"));
            }
        }

        return entries;
    }
}
